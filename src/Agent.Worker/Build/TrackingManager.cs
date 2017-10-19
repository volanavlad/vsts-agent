﻿using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Globalization;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    [ServiceLocator(Default = typeof(TrackingManager))]
    public interface ITrackingManager : IAgentService
    {
        TrackingConfig Create(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            string hashKey,
            string file,
            bool overrideBuildDirectory);

        TrackingConfigBase LoadIfExists(IExecutionContext executionContext, string file);

        void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config);

        void UpdateJobRunProperties(IExecutionContext executionContext, TrackingConfig config, string file);

        void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration);

        void DisposeCollectedGarbage(IExecutionContext executionContext);

        void MaintenanceStarted(TrackingConfig config, string file);

        void MaintenanceCompleted(TrackingConfig config, string file);
    }

    public sealed class TrackingManager : AgentService, ITrackingManager
    {
        public TrackingConfig Create(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            string hashKey,
            string file,
            bool overrideBuildDirectory)
        {
            Trace.Entering();

            // Get or create the top-level tracking config.
            TopLevelTrackingConfig topLevelConfig;
            string topLevelFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.TopLevelTrackingConfigFile);
            Trace.Verbose($"Loading top-level tracking config if exists: {topLevelFile}");
            if (!File.Exists(topLevelFile))
            {
                topLevelConfig = new TopLevelTrackingConfig();
            }
            else
            {
                topLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    executionContext.Warning($"Rebuild corruptted top-level tracking configure file {topLevelFile}.");
                    // save the corruptted file in case we need to investigate more.
                    File.Copy(topLevelFile, $"{topLevelFile}.corruptted", true);

                    topLevelConfig = new TopLevelTrackingConfig();
                    DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));

                    foreach (var dir in workDir.EnumerateDirectories())
                    {
                        // we scan the entire _work directory and find the directory with the highest integer number.
                        if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                            lastBuildNumber > topLevelConfig.LastBuildDirectoryNumber)
                        {
                            topLevelConfig.LastBuildDirectoryNumber = lastBuildNumber;
                        }
                    }
                }
            }

            // Determine the build directory.
            if (overrideBuildDirectory)
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.
                ArgUtil.Equal(default(int), topLevelConfig.LastBuildDirectoryNumber, nameof(topLevelConfig.LastBuildDirectoryNumber));
                var configurationStore = HostContext.GetService<IConfigurationStore>();
                AgentSettings settings = configurationStore.GetSettings();
                topLevelConfig.LastBuildDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastBuildDirectoryNumber++;
            }

            // Update the top-level tracking config.
            topLevelConfig.LastBuildDirectoryCreatedOn = DateTimeOffset.Now;
            WriteToFile(topLevelFile, topLevelConfig);

            // Create the new tracking config.
            TrackingConfig config = new TrackingConfig(
                executionContext,
                endpoint,
                topLevelConfig.LastBuildDirectoryNumber,
                hashKey);
            WriteToFile(file, config);
            return config;
        }

        public TrackingConfigBase LoadIfExists(IExecutionContext executionContext, string file)
        {
            Trace.Entering();

            // The tracking config will not exist for a new definition.
            if (!File.Exists(file))
            {
                return null;
            }

            // Load the content and distinguish between tracking config file
            // version 1 and file version 2.
            string content = File.ReadAllText(file);
            string fileFormatVersionJsonProperty = StringUtil.Format(
                @"""{0}""",
                TrackingConfig.FileFormatVersionJsonProperty);
            if (content.Contains(fileFormatVersionJsonProperty))
            {
                // The config is the new format.
                Trace.Verbose("Parsing new tracking config format.");
                return JsonConvert.DeserializeObject<TrackingConfig>(content);
            }

            // Attempt to parse the legacy format.
            Trace.Verbose("Parsing legacy tracking config format.");
            LegacyTrackingConfig config = LegacyTrackingConfig.TryParse(content);
            if (config == null)
            {
                executionContext.Warning(StringUtil.Loc("UnableToParseBuildTrackingConfig0", content));
            }

            return config;
        }

        public void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config)
        {
            Trace.Entering();

            // Convert legacy format to the new format.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            if (legacyConfig != null)
            {
                // Convert legacy format to the new format.
                config = new TrackingConfig(
                    executionContext,
                    legacyConfig,
                    // The repository type and sources folder wasn't stored in the legacy format - only the
                    // build folder was stored. Since the hash key has changed, it is
                    // unknown what the source folder was named. Just set the folder name
                    // to "s" so the property isn't left blank. 
                    repositoryType: string.Empty,
                    sourcesDirectoryNameOnly: Constants.Build.Path.SourcesDirectory);
            }

            // Write a copy of the tracking config to the GC folder.
            string gcDirectory = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);
            string file = Path.Combine(
                gcDirectory,
                StringUtil.Format("{0}.json", Guid.NewGuid()));
            WriteToFile(file, config);
        }

        public void UpdateJobRunProperties(IExecutionContext executionContext, TrackingConfig config, string file)
        {
            Trace.Entering();

            // Update the info properties and save the file.
            config.UpdateJobRunProperties(executionContext);
            WriteToFile(file, config);
        }

        public void MaintenanceStarted(TrackingConfig config, string file)
        {
            Trace.Entering();
            config.LastMaintenanceAttemptedOn = DateTimeOffset.Now;
            config.LastMaintenanceCompletedOn = null;
            WriteToFile(file, config);
        }

        public void MaintenanceCompleted(TrackingConfig config, string file)
        {
            Trace.Entering();
            config.LastMaintenanceCompletedOn = DateTimeOffset.Now;
            WriteToFile(file, config);
        }

        public void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration)
        {
            Trace.Entering();
            Trace.Info("Scan all SourceFolder tracking files.");
            string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            if (!Directory.Exists(searchRoot))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
                return;
            }

            var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
            Trace.Verbose($"Find {allTrackingFiles.Count()} tracking files.");

            executionContext.Output(StringUtil.Loc("DirExpireLimit", expiration.TotalDays));
            executionContext.Output(StringUtil.Loc("CurrentUTC", DateTime.UtcNow.ToString("o")));

            // scan all sourcefolder tracking file, find which folder has never been used since UTC-expiration
            // the scan and garbage discovery should be best effort.
            // if the tracking file is in old format, just delete the folder since the first time the folder been use we will convert the tracking file to new format.
            foreach (var trackingFile in allTrackingFiles)
            {
                try
                {
                    executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", trackingFile));
                    TrackingConfigBase tracking = LoadIfExists(executionContext, trackingFile);

                    // detect whether the tracking file is in new format.
                    TrackingConfig newTracking = tracking as TrackingConfig;
                    if (newTracking == null)
                    {
                        LegacyTrackingConfig legacyConfig = tracking as LegacyTrackingConfig;
                        ArgUtil.NotNull(legacyConfig, nameof(LegacyTrackingConfig));

                        Trace.Verbose($"{trackingFile} is a old format tracking file.");

                        executionContext.Output(StringUtil.Loc("GCOldFormatTrackingFile", trackingFile));
                        MarkForGarbageCollection(executionContext, legacyConfig);
                        IOUtil.DeleteFile(trackingFile);
                    }
                    else
                    {
                        Trace.Verbose($"{trackingFile} is a new format tracking file.");
                        ArgUtil.NotNull(newTracking.LastRunOn, nameof(newTracking.LastRunOn));
                        executionContext.Output(StringUtil.Loc("BuildDirLastUseTIme", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), newTracking.BuildDirectory), newTracking.LastRunOnString));
                        if (DateTime.UtcNow - expiration > newTracking.LastRunOn)
                        {
                            executionContext.Output(StringUtil.Loc("GCUnusedTrackingFile", trackingFile, expiration.TotalDays));
                            MarkForGarbageCollection(executionContext, newTracking);
                            IOUtil.DeleteFile(trackingFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", trackingFile));
                    executionContext.Error(ex);
                }
            }
        }

        public void DisposeCollectedGarbage(IExecutionContext executionContext)
        {
            Trace.Entering();
            PrintOutDiskUsage(executionContext);

            string gcDirectory = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);

            if (!Directory.Exists(gcDirectory))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", gcDirectory));
                return;
            }

            IEnumerable<string> gcTrackingFiles = Directory.EnumerateFiles(gcDirectory, "*.json");
            if (gcTrackingFiles == null || gcTrackingFiles.Count() == 0)
            {
                executionContext.Output(StringUtil.Loc("GCDirIsEmpty", gcDirectory));
                return;
            }

            Trace.Info($"Find {gcTrackingFiles.Count()} GC tracking files.");

            if (gcTrackingFiles.Count() > 0)
            {
                foreach (string gcFile in gcTrackingFiles)
                {
                    if (executionContext.CancellationToken.IsCancellationRequested)
                    {
                        // maintenance has been cancelled.
                        break;
                    }

                    try
                    {
                        var gcConfig = LoadIfExists(executionContext, gcFile) as TrackingConfig;
                        ArgUtil.NotNull(gcConfig, nameof(TrackingConfig));

                        string fullPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), gcConfig.BuildDirectory);
                        executionContext.Output(StringUtil.Loc("Deleting", fullPath));
                        IOUtil.DeleteDirectory(fullPath, executionContext.CancellationToken);

                        executionContext.Output(StringUtil.Loc("DeleteGCTrackingFile", fullPath));
                        IOUtil.DeleteFile(gcFile);
                    }
                    catch (Exception ex)
                    {
                        executionContext.Error(StringUtil.Loc("ErrorDuringBuildGCDelete", gcFile));
                        executionContext.Error(ex);
                    }
                }

                PrintOutDiskUsage(executionContext);
            }
        }

        private void PrintOutDiskUsage(IExecutionContext context)
        {
            // Print disk usage should be best effort, since DriveInfo can't detect usage of UNC share.
            try
            {
                context.Output($"Disk usage for working directory: {HostContext.GetDirectory(WellKnownDirectory.Work)}");
                var workDirectoryDrive = new DriveInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                long freeSpace = workDirectoryDrive.AvailableFreeSpace;
                long totalSpace = workDirectoryDrive.TotalSize;
#if OS_WINDOWS
                context.Output($"Working directory belongs to drive: '{workDirectoryDrive.Name}'");
#else
                context.Output($"Information about file system on which working directory resides.");
#endif
                context.Output($"Total size: '{totalSpace / 1024.0 / 1024.0} MB'");
                context.Output($"Available space: '{freeSpace / 1024.0 / 1024.0} MB'");
            }
            catch (Exception ex)
            {
                context.Warning($"Unable inspect disk usage for working directory {HostContext.GetDirectory(WellKnownDirectory.Work)}.");
                Trace.Error(ex);
                context.Debug(ex.ToString());
            }
        }

        private void WriteToFile(string file, object value)
        {
            Trace.Entering();
            Trace.Verbose($"Writing config to file: {file}");

            // Create the directory if it does not exist.
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            IOUtil.SaveObject(value, file);
        }
    }
}