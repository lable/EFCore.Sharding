﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace EFCore.Sharding
{
    internal class ShardingDbAccessor : IShardingDbAccessor
    {
        private readonly IShardingConfig _shardingConfig;
        private readonly IDbFactory _dbFactory;

        #region 构造函数

        public ShardingDbAccessor(IShardingConfig shardingConfig, IDbFactory dbFactory)
        {
            _shardingConfig = shardingConfig;
            _dbFactory = dbFactory;
            _db = _dbFactory.GetDbAccessor(string.Empty, shardingConfig.FindADbType());
        }

        #endregion

        #region 私有成员

        private IDbAccessor _db { get; }
        private string GetDbId(string conString, DatabaseType dbType, string suffix)
        {
            return $"{conString}{dbType}{suffix}";
        }
        private async Task<int> PackAccessDataAsync(Func<Task<int>> access)
        {
            var dbs = _dbs.Values.ToArray();

            int count = 0;
            if (!OpenedTransaction)
            {
                using (var transaction = DistributedTransactionFactory.GetDistributedTransaction())
                {
                    transaction.AddDbAccessor(dbs);

                    var (Success, ex) = await transaction.RunTransactionAsync(async () =>
                    {
                        count = await access();
                    });
                    if (!Success)
                        throw ex;
                }
                ClearDbs();
                return count;
            }
            else
            {
                _transaction.AddDbAccessor(dbs);
                count = await access();
            }

            return count;
        }
        private async Task<int> WriteTableAsync<T>(List<T> entities, Func<T, IDbAccessor, Task<int>> accessDataAsync)
        {
            List<(T obj, IDbAccessor db)> targetDbs = entities
                .Select(x => new
                {
                    Obj = x,
                    Conifg = _shardingConfig.GetTheWriteTable(x)
                })
                .ToList()
                .Select(x => (x.Obj, GetMapDbAccessor(x.Conifg.conString, x.Conifg.dbType, x.Conifg.suffix)))
                .ToList();

            return await PackAccessDataAsync(async () =>
            {
                //同一个IDbAccessor对象只能在一个线程中
                List<Task<int>> tasks = new List<Task<int>>();
                var dbs = targetDbs.Select(x => x.db).Distinct().ToList();
                dbs.ForEach(aDb =>
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        int count = 0;
                        var objs = targetDbs.Where(x => x.db == aDb).ToList();
                        foreach (var aObj in objs)
                        {
                            count += await accessDataAsync(aObj.obj, aObj.db);
                        }

                        return count;
                    }));
                });

                return (await Task.WhenAll(tasks.ToArray())).Sum();
            });
        }
        private DistributedTransaction _transaction { get; set; }
        private ConcurrentDictionary<string, IDbAccessor> _dbs { get; }
            = new ConcurrentDictionary<string, IDbAccessor>();
        private void ClearDbs()
        {
            _dbs.ForEach(x => x.Value.Dispose());
            _dbs.Clear();
        }

        #endregion

        #region 外部接口

        public bool OpenedTransaction { get; set; } = false;
        public IDbAccessor GetMapDbAccessor(string conString, DatabaseType dbType, string suffix)
        {
            var dbId = GetDbId(conString, dbType, suffix);
            IDbAccessor db = _dbs.GetOrAdd(dbId, key => _dbFactory.GetDbAccessor(conString, dbType, null, suffix));

            if (OpenedTransaction)
                _transaction.AddDbAccessor(db);

            return db;
        }
        public int Insert<T>(T entity) where T : class
        {
            return Insert(new List<T> { entity });
        }
        public async Task<int> InsertAsync<T>(T entity) where T : class
        {
            return await InsertAsync(new List<T> { entity });
        }
        public int Insert<T>(List<T> entities) where T : class
        {
            return AsyncHelper.RunSync(() => InsertAsync(entities));
        }
        public async Task<int> InsertAsync<T>(List<T> entities) where T : class
        {
            return await WriteTableAsync(entities, (targetObj, targetDb) => targetDb.InsertAsync(targetObj));
        }
        public int DeleteAll<T>() where T : class
        {
            return AsyncHelper.RunSync(() => DeleteAllAsync<T>());
        }
        public async Task<int> DeleteAllAsync<T>() where T : class
        {
            var configs = _shardingConfig.GetWriteTables<T>();
            return await PackAccessDataAsync(async () =>
            {
                var tasks = configs.Select(x => GetMapDbAccessor(x.conString, x.dbType, x.suffix).DeleteAllAsync<T>());
                return (await Task.WhenAll(tasks.ToArray())).Sum();
            });
        }
        public int Delete<T>(T entity) where T : class
        {
            return Delete(new List<T> { entity });
        }
        public async Task<int> DeleteAsync<T>(T entity) where T : class
        {
            return await DeleteAsync(new List<T> { entity });
        }
        public int Delete<T>(List<T> entities) where T : class
        {
            return AsyncHelper.RunSync(() => DeleteAsync(entities));
        }
        public async Task<int> DeleteAsync<T>(List<T> entities) where T : class
        {
            return await WriteTableAsync(entities, (targetObj, targetDb) => targetDb.DeleteAsync(targetObj));
        }
        public int Delete<T>(Expression<Func<T, bool>> condition) where T : class
        {
            return AsyncHelper.RunSync(() => DeleteAsync(condition));
        }
        public async Task<int> DeleteAsync<T>(Expression<Func<T, bool>> condition) where T : class
        {
            var deleteList = GetIShardingQueryable<T>().Where(condition).ToList();

            return await DeleteAsync(deleteList);
        }
        public int Delete_Sql<T>(Expression<Func<T, bool>> where) where T : class
        {
            return AsyncHelper.RunSync(() => Delete_SqlAsync(where));
        }
        public async Task<int> Delete_SqlAsync<T>(Expression<Func<T, bool>> where) where T : class
        {
            var q = _db.GetIQueryable<T>().Where(where);
            var configs = _shardingConfig.GetWriteTables<T>(q);
            return await PackAccessDataAsync(async () =>
            {
                var tasks = configs.Select(x => GetMapDbAccessor(x.conString, x.dbType, x.suffix).Delete_SqlAsync<T>(where));
                return (await Task.WhenAll(tasks.ToArray())).Sum();
            });
        }
        public int Update<T>(T entity) where T : class
        {
            return Update(new List<T> { entity });
        }
        public async Task<int> UpdateAsync<T>(T entity) where T : class
        {
            return await UpdateAsync(new List<T> { entity });
        }
        public int Update<T>(List<T> entities) where T : class
        {
            return AsyncHelper.RunSync(() => UpdateAsync(entities));
        }
        public async Task<int> UpdateAsync<T>(List<T> entities) where T : class
        {
            return await WriteTableAsync(entities, (targetObj, targetDb) => targetDb.UpdateAsync(targetObj));
        }
        public int Update<T>(T entity, List<string> properties) where T : class
        {
            return Update(new List<T> { entity }, properties);
        }
        public async Task<int> UpdateAsync<T>(T entity, List<string> properties) where T : class
        {
            return await UpdateAsync(new List<T> { entity }, properties);
        }
        public int Update<T>(List<T> entities, List<string> properties) where T : class
        {
            return AsyncHelper.RunSync(() => UpdateAsync(entities, properties));
        }
        public async Task<int> UpdateAsync<T>(List<T> entities, List<string> properties) where T : class
        {
            return await WriteTableAsync(entities, (targetObj, targetDb) => targetDb.UpdateAsync(targetObj, properties));
        }
        public int Update<T>(Expression<Func<T, bool>> whereExpre, Action<T> set) where T : class
        {
            return AsyncHelper.RunSync(() => UpdateAsync(whereExpre, set));
        }
        public async Task<int> UpdateAsync<T>(Expression<Func<T, bool>> whereExpre, Action<T> set) where T : class
        {
            var list = GetIShardingQueryable<T>().Where(whereExpre).ToList();
            list.ForEach(aData => set(aData));
            return await UpdateAsync(list);
        }
        public IShardingQueryable<T> GetIShardingQueryable<T>() where T : class
        {
            return new ShardingQueryable<T>(_db.GetIQueryable<T>(), this, _shardingConfig, _dbFactory);
        }
        public List<T> GetList<T>() where T : class
        {
            return GetIShardingQueryable<T>().ToList();
        }
        public async Task<List<T>> GetListAsync<T>() where T : class
        {
            return await GetIShardingQueryable<T>().ToListAsync();
        }

        #endregion

        #region 事物处理

        public (bool Success, Exception ex) RunTransaction(Action action, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            bool isOK = true;
            Exception resEx = null;
            try
            {
                BeginTransaction(isolationLevel);

                action();

                CommitTransaction();
            }
            catch (Exception ex)
            {
                RollbackTransaction();
                isOK = false;
                resEx = ex;
            }
            finally
            {
                DisposeTransaction();
            }

            return (isOK, resEx);
        }
        public async Task<(bool Success, Exception ex)> RunTransactionAsync(Func<Task> action, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            bool isOK = true;
            Exception resEx = null;
            try
            {
                await BeginTransactionAsync(isolationLevel);

                await action();

                CommitTransaction();
            }
            catch (Exception ex)
            {
                RollbackTransaction();
                isOK = false;
                resEx = ex;
            }
            finally
            {
                DisposeTransaction();
            }

            return (isOK, resEx);
        }
        public void BeginTransaction(IsolationLevel isolationLevel)
        {
            OpenedTransaction = true;
            _transaction = new DistributedTransaction();
            _transaction.BeginTransaction(isolationLevel);
        }
        public async Task BeginTransactionAsync(IsolationLevel isolationLevel)
        {
            OpenedTransaction = true;
            _transaction = new DistributedTransaction();
            await _transaction.BeginTransactionAsync(isolationLevel);
        }
        public void CommitTransaction()
        {
            _transaction.CommitTransaction();
        }
        public void RollbackTransaction()
        {
            _transaction.RollbackTransaction();
        }
        public void DisposeTransaction()
        {
            OpenedTransaction = false;
            _transaction.DisposeTransaction();
            ClearDbs();
        }

        #endregion

        #region Dispose

        private bool _disposed = false;
        public virtual void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _transaction?.Dispose();
            ClearDbs();
        }

        #endregion
    }
}
