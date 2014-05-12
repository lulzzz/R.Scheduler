﻿using System;
using System.IO;
using System.Reflection;
using log4net;
using Quartz;
using R.MessageBus.Interfaces;
using R.Scheduler.Contracts.Interfaces;
using R.Scheduler.Contracts.Messages;
using StructureMap;

namespace R.Scheduler.JobRunners
{
    /// <summary>
    /// PluginRunner loads and executes JobPlugins within a separate AppDomain.
    /// </summary>
    public class PluginRunner : IJob
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public IBus Bus { get; set; }

        /// <summary>
        /// Ctor used by Scheduler engine
        /// </summary>
        public PluginRunner()
        {
            Bus = ObjectFactory.Container.GetInstance<IBus>();
        }

        /// <summary>
        /// Ctor used for testing. Allows injecting a mock/fake instance of IBus.
        /// </summary>
        /// <param name="bus"></param>
        public PluginRunner(IBus bus)
        {
            Bus = bus;
        }

        /// <summary>
        /// Entry point into the job execution.
        /// </summary>
        /// <param name="context"></param>
        public void Execute(IJobExecutionContext context)
        {
            JobDataMap dataMap = context.JobDetail.JobDataMap;

            string pluginPath = dataMap.GetString("pluginPath");

            if (string.IsNullOrEmpty(pluginPath) || !File.Exists(pluginPath))
            {
                Logger.Error(string.Format("plugin file '{0}' does not exist.", pluginPath));
                return;
            }

            var pluginAssemblyName = Path.GetFileNameWithoutExtension(pluginPath);

            var appDomain = GetAppDomain(pluginPath, pluginAssemblyName);
            var pluginTypeName = GetPluginTypeName(appDomain, pluginPath);
            var jobPlugin = appDomain.CreateInstanceAndUnwrap(pluginAssemblyName, pluginTypeName) as IJobPlugin;

            bool success = false;
            try
            {
                if (jobPlugin != null)
                {
                    jobPlugin.Execute();
                    success = true;
                }
                else
                {
                    Logger.Error(string.Format("Plugin cannot be null {0}.", pluginTypeName));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Error occured in {0}.", pluginTypeName), ex);
            }

            Bus.Publish(new JobExecutedMessage(Guid.NewGuid()) { Success = success, Timestamp = DateTime.UtcNow, Type = pluginTypeName });

            AppDomain.Unload(appDomain);
        }

        #region Private Methods

        private static AppDomain GetAppDomain(string pluginPath, string pluginAssemblyName)
        {
            var appBase = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            var assemblyFolderPath = Path.GetDirectoryName(pluginPath);
            var privateBinPath = assemblyFolderPath;

            if (assemblyFolderPath != null && assemblyFolderPath.StartsWith(appBase))
            {
                privateBinPath = assemblyFolderPath.Replace(appBase, string.Empty);
                if (privateBinPath.StartsWith(@"\"))
                    privateBinPath = privateBinPath.Substring(1);
            }

            var setup = new AppDomainSetup
            {
                ApplicationBase = appBase,
                PrivateBinPath = privateBinPath,
                ShadowCopyFiles = "true",
                ShadowCopyDirectories = assemblyFolderPath,
                LoaderOptimization = LoaderOptimization.MultiDomainHost
            };

            var appDomain = AppDomain.CreateDomain(Guid.NewGuid() + "_" + pluginAssemblyName, null, setup);

            return appDomain;
        }

        private static string GetPluginTypeName(AppDomain domain, string pluginPath)
        {
            PluginAppDomainHelper helper = null;
            var pluginFinderType = typeof (PluginAppDomainHelper);

            if (!string.IsNullOrEmpty(pluginFinderType.FullName))
                helper =
                    domain.CreateInstanceAndUnwrap(pluginFinderType.Assembly.FullName, pluginFinderType.FullName, false,
                        BindingFlags.CreateInstance, null, new object[] {pluginPath}, null, null) as
                        PluginAppDomainHelper;

            if (helper == null)
                throw new Exception("Couldn't create plugin domain helper");

            return helper.PluginTypeName;
        }

        #endregion
    }
}
