using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace EFCore.Sharding.Tests
{
    [TestClass]
    public class Startup
    {
        /// <summary>
        /// 所有单元测试开始前
        /// </summary>
        /// <param name="context"></param>
        [AssemblyInitialize]
        public static void Begin(TestContext context)
        {
            ServiceCollection services = new ServiceCollection();
            services.AddEFCoreSharding(config =>
            {
                config.UseDatabase(Config.CONSTRING1, DatabaseType.SqlServer);
                config.UseDatabase<ISQLiteDb1>(Config.SQLITE1, DatabaseType.SQLite);
                config.UseDatabase<ISQLiteDb2>(Config.SQLITE2, DatabaseType.SQLite);
                config.UseDatabase<ICustomDbAccessor>(Config.CONSTRING1, DatabaseType.SqlServer);

                //分表配置
                //添加数据源
                config.AddDataSource(Config.CONSTRING1, ReadWriteType.Read | ReadWriteType.Write, DatabaseType.SqlServer);
                //设置分表规则
                config.SetHashModSharding<Base_UnitTest>(nameof(Base_UnitTest.Id), 3);
                

                // y 测试 jsonb
                DateTime startTime = DateTime.Now.AddMinutes(-5);
                DateTime endTime = DateTime.MaxValue;
                config.AddAbsDb(DatabaseType.PostgreSql, absDbName: "postgres")
                    .AddPhysicDb(ReadWriteType.Read | ReadWriteType.Write, "server=localhost;uid=postgres;password=;database=test;port=5433;commandtimeout=1024;", groupName: "postgres")
                    .AddPhysicDbGroup(groupName: "postgres", absDbName: "postgres")
                    .SetDateShardingRule<SqlDefaultTestModel>(nameof(SqlDefaultTestModel.ModifiedOn), absDbName: "postgres")//设置分表规则
                    .AutoExpandByDate<SqlDefaultTestModel>(//设置为按时间自动分表
                        ExpandByDateMode.PerMonth,
                        (startTime, endTime, groupName: "postgres")
                        );
            });

            ServiceProvider = services.BuildServiceProvider();
        }

        /// <summary>
        /// 所有单元测试结束后
        /// </summary>
        [AssemblyCleanup]
        public static void End()
        {

        }

        public static IServiceProvider ServiceProvider;
    }
}
