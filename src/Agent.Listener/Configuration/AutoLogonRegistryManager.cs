#if OS_WINDOWS
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(AutoLogonRegistryManager))]
    public interface IAutoLogonRegistryManager : IAgentService
    {        
        void UpdateRegistrySettings(CommandSettings command, string domainName, string userName, string logonPassword);
        void RevertRegistrySettings(string domainName, string userName);
    }

    public class AutoLogonRegistryManager : AgentService, IAutoLogonRegistryManager
    {
        private IWindowsRegistryManager _registryManager;
        private INativeWindowsServiceHelper _windowsServiceHelper;
        private ITerminal _terminal;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _registryManager = hostContext.GetService<IWindowsRegistryManager>();
            _windowsServiceHelper = hostContext.GetService<INativeWindowsServiceHelper>();
            _terminal = HostContext.GetService<ITerminal>();
        }

        public void UpdateRegistrySettings(CommandSettings command, string domainName, string userName, string logonPassword)
        {
            IntPtr userHandler = IntPtr.Zero;
            PROFILEINFO userProfile = new PROFILEINFO();
            try
            {
                string securityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
                if(string.IsNullOrEmpty(securityId))
                {
                    Trace.Error($"Security Id not found for the user ({domainName}\\{userName}). Cannot proceed with autologon configuration.");
                    throw new InvalidOperationException(StringUtil.Loc("SIdNotFound", domainName, userName, "configuration"));
                }

                //check if the registry exists for the user, if not load the user profile
                if(!_registryManager.SubKeyExists(RegistryHive.Users, securityId))
                {
                    userProfile.dwSize = Marshal.SizeOf(typeof(PROFILEINFO));
                    userProfile.lpUserName = userName;

                    _windowsServiceHelper.LoadUserProfile(domainName, userName, logonPassword, out userHandler, out userProfile);
                }

                if(!_registryManager.SubKeyExists(RegistryHive.Users, securityId))
                {
                    throw new InvalidOperationException(StringUtil.Loc("ProfileLoadFailure", domainName, userName));
                }

                ShowAutoLogonWarningIfAlreadyEnabled(domainName, userName);

                //machine specific
                UpdateMachineSpecificRegistrySettings(domainName, userName);

                //user specific
                UpdateUserSpecificRegistrySettings(command, securityId);
            }
            finally
            {
                if(userHandler != IntPtr.Zero)
                {
                    _windowsServiceHelper.UnloadUserProfile(userHandler, userProfile);
                }
            }
        }

        public void RevertRegistrySettings(string domainName, string userName)
        {
            string securityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
            if(string.IsNullOrEmpty(securityId))
            {
                Trace.Error($"Security Id not found for the user ({domainName}\\{userName}). Cannot proceed with autologon unconfiguration.");
                throw new InvalidOperationException(StringUtil.Loc("SIdNotFound", domainName, userName, "unconfiguration"));
            }

            //machine specific            
            RevertAutoLogonSpecificSettings(domainName, userName);

            //user specific
            RevertUserSpecificSettings(RegistryHive.Users, securityId);
        }

        private void RevertAutoLogonSpecificSettings(string domainName, string userName)
        {
            var actualDomainNameForAutoLogon = _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                                            RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                                                            RegistryConstants.MachineSettings.ValueNames.AutoLogonDomainName);

            var actualUserNameForAutoLogon = _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                                            RegistryConstants.MachineSettings.SubKeys.AutoLogon, 
                                                                            RegistryConstants.MachineSettings.ValueNames.AutoLogonUserName);

            if(string.Equals(actualDomainNameForAutoLogon, domainName, StringComparison.CurrentCultureIgnoreCase)
                ||string.Equals(actualUserNameForAutoLogon, userName, StringComparison.CurrentCultureIgnoreCase))
            {
                RevertOriginalValue(RegistryHive.LocalMachine, 
                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon, 
                                        RegistryConstants.MachineSettings.ValueNames.AutoLogon);
                RevertOriginalValue(RegistryHive.LocalMachine,
                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                        RegistryConstants.MachineSettings.ValueNames.AutoLogonUserName);
                RevertOriginalValue(RegistryHive.LocalMachine,
                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                        RegistryConstants.MachineSettings.ValueNames.AutoLogonDomainName);
                RevertOriginalValue(RegistryHive.LocalMachine,
                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                        RegistryConstants.MachineSettings.ValueNames.AutoLogonPassword);
                RevertOriginalValue(RegistryHive.LocalMachine, 
                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                        RegistryConstants.MachineSettings.ValueNames.AutoLogonCount);
            }
            else
            {
                Trace.Info("AutoLogon user and/or domain name is not same as expected after autologon configuration.");
                Trace.Info($"Actual values: Domain - {actualDomainNameForAutoLogon}, user - {actualUserNameForAutoLogon}");
                Trace.Info($"Expected values: Domain - {domainName}, user - {userName}");
                Trace.Info("Skipping the revert of autologon settings.");
            }
        }

        private void UpdateMachineSpecificRegistrySettings(string domainName, string userName)
        {
            var hive = RegistryHive.LocalMachine;
            //before enabling autologon, inspect the policies that may affect it and log the warning
            InspectAutoLogonRelatedPolicies();

            TakeBackupAndSetValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogonUserName, userName);
            TakeBackupAndSetValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogonDomainName, domainName);

            //this call is to take the backup of the password key if already exists as we delete the key in the next step
            TakeBackupAndSetValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogonPassword, "");
            _registryManager.DeleteValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogonPassword);
            
            TakeBackupAndSetValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogonCount, "");
            _registryManager.DeleteValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogonCount);

            TakeBackupAndSetValue(hive, RegistryConstants.MachineSettings.SubKeys.AutoLogon, RegistryConstants.MachineSettings.ValueNames.AutoLogon, "1");
        }

        private void InspectAutoLogonRelatedPolicies()
        {
            Trace.Info("Checking for policies that may prevent autologon from working correctly.");
            _terminal.WriteLine(StringUtil.Loc("AutoLogonPoliciesInspection"));

            var warningReasons = new List<string>();
            if (_registryManager.SubKeyExists(RegistryHive.LocalMachine, RegistryConstants.MachineSettings.SubKeys.ShutdownReasonDomainPolicy))
            {
                //shutdown reason
                var shutdownReasonValue = _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                                        RegistryConstants.MachineSettings.SubKeys.ShutdownReasonDomainPolicy, 
                                                                        RegistryConstants.MachineSettings.ValueNames.ShutdownReason);
                if (int.TryParse(shutdownReasonValue, out int shutdownReasonOn) 
                        && shutdownReasonOn == 1)
                {
                    warningReasons.Add(StringUtil.Loc("AutoLogonPolicies_ShutdownReason"));
                }
            }

            
            if (_registryManager.SubKeyExists(RegistryHive.LocalMachine, RegistryConstants.MachineSettings.SubKeys.LegalNotice))
            {
                //legal caption/text
                var legalNoticeCaption = _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                                    RegistryConstants.MachineSettings.SubKeys.LegalNotice,
                                                                    RegistryConstants.MachineSettings.ValueNames.LegalNoticeCaption);
                var legalNoticeText =  _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                                    RegistryConstants.MachineSettings.SubKeys.LegalNotice, 
                                                                    RegistryConstants.MachineSettings.ValueNames.LegalNoticeText);
                if (!string.IsNullOrEmpty(legalNoticeCaption) || !string.IsNullOrEmpty(legalNoticeText))
                {
                    warningReasons.Add(StringUtil.Loc("AutoLogonPolicies_LegalNotice"));
                }
            }
            
            if (warningReasons.Count > 0)
            {
                Trace.Warning("Following policies may affect the autologon:");
                _terminal.WriteError(StringUtil.Loc("AutoLogonPoliciesWarningsHeader"));
                for (int i=0; i < warningReasons.Count; i++)
                {
                    var msg = String.Format("{0} - {1}", i + 1, warningReasons[i]);
                    Trace.Warning(msg);
                    _terminal.WriteError(msg);
                }
                _terminal.WriteLine();
            }
        }

        private void UpdateUserSpecificRegistrySettings(CommandSettings command, string securityId)
        {
            //User specific
            UpdateScreenSaverSettings(command, securityId);
            
            //User specific
            string subKeyName = $"{securityId}\\{RegistryConstants.UserSettings.SubKeys.StartupProcess}";
            TakeBackupAndSetValue(RegistryHive.Users, subKeyName, RegistryConstants.UserSettings.ValueNames.StartupProcess, GetStartupCommand());
        }

        private void UpdateScreenSaverSettings(CommandSettings command, string securityId= null)
        {
            if(!command.GetDisableScreenSaver())
            {
                return;
            }

            Trace.Info("Checking for policies that may prevent screensaver from being disabled.");
            _terminal.WriteLine(StringUtil.Loc("ScreenSaverPoliciesInspection"));

            string subKeyName = $"{securityId}\\{RegistryConstants.UserSettings.SubKeys.ScreenSaverDomainPolicy}";
            if(_registryManager.SubKeyExists(RegistryHive.Users, subKeyName))
            {            
                var screenSaverValue = _registryManager.GetValue(RegistryHive.Users, subKeyName, RegistryConstants.UserSettings.ValueNames.ScreenSaver);
                if (int.TryParse(screenSaverValue, out int isScreenSaverDomainPolicySet)
                        && isScreenSaverDomainPolicySet == 1)
                {
                    Trace.Warning("Screensaver policy is defined on the machine. Screensaver may not remain disabled always.");
                    _terminal.WriteError(StringUtil.Loc("ScreenSaverPolicyWarning"));
                }
            }

            string screenSaverSubKeyName = $"{securityId}\\{RegistryConstants.UserSettings.SubKeys.ScreenSaver}";
            TakeBackupAndSetValue(RegistryHive.Users, screenSaverSubKeyName, RegistryConstants.UserSettings.ValueNames.ScreenSaver, "0");
        }

        private string GetStartupCommand()
        {
            //startup process            
            string cmdExePath = System.Environment.GetEnvironmentVariable("comspec");
            if (string.IsNullOrEmpty(cmdExePath))
            {
                cmdExePath = "cmd.exe";
            }

            //file to run in cmd.exe
            var filePath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), "run.cmd");

            //extra " are to handle the spaces in the file path (if any)
            var startupCommand = $@"{cmdExePath} /D /S /C start ""Agent with AutoLogon"" ""{filePath}""";
            Trace.Info($"Setting startup command as '{startupCommand}'");

            return startupCommand;
        }

        private void TakeBackupAndSetValue(RegistryHive targetHive, string subKeyName, string name, string value)
        {
            //take backup if it exists
            string origValue = _registryManager.GetValue(targetHive, subKeyName, name);
            if (!string.IsNullOrEmpty(origValue))
            {
                var nameForTheBackupValue = GetBackupValueName(name);
                _registryManager.SetValue(targetHive, subKeyName, nameForTheBackupValue, origValue);
            }

            _registryManager.SetValue(targetHive, subKeyName, name, value);
        }
        
        private void RevertUserSpecificSettings(RegistryHive targetHive, string securityId)
        {
            var screenSaverSubKey = $"{securityId}\\{RegistryConstants.UserSettings.SubKeys.ScreenSaver}";
            var currentValue = _registryManager.GetValue(targetHive, screenSaverSubKey, RegistryConstants.UserSettings.ValueNames.ScreenSaver);

            if(string.Equals(currentValue, "0", StringComparison.CurrentCultureIgnoreCase))
            {
                RevertOriginalValue(targetHive, screenSaverSubKey, RegistryConstants.UserSettings.ValueNames.ScreenSaver);
            }
            else
            {
                Trace.Info($"Screensaver setting value was not same as expected after autologon configuration. Actual - {currentValue}, Expected - 0. Skipping the revert of it.");
            }
            
            var startupProcessSubKeyName = $"{securityId}\\{RegistryConstants.UserSettings.SubKeys.StartupProcess}";
            var expectedStartupCmd = GetStartupCommand();
            var actualStartupCmd = _registryManager.GetValue(targetHive, startupProcessSubKeyName, RegistryConstants.UserSettings.ValueNames.StartupProcess);

            if(string.Equals(actualStartupCmd, expectedStartupCmd, StringComparison.CurrentCultureIgnoreCase))
            {
                RevertOriginalValue(targetHive, 
                                        $"{securityId}\\{RegistryConstants.UserSettings.SubKeys.StartupProcess}",
                                        RegistryConstants.UserSettings.ValueNames.StartupProcess);
            }
            else
            {
                Trace.Info($"Startup process command is not same as expected after autologon configuration. Skipping the revert of it.");
                Trace.Info($"Actual - {actualStartupCmd}, Expected - {expectedStartupCmd}.");
            }
        }

        private void RevertOriginalValue(RegistryHive targetHive, string subKeyName, string name)
        {
            var nameofTheBackupValue = GetBackupValueName(name);
            var originalValue = _registryManager.GetValue(targetHive, subKeyName, nameofTheBackupValue);
            
            Trace.Info($"Reverting the registry setting. Hive - {targetHive}, subKeyName - {subKeyName}, name - {name}");
            if (string.IsNullOrEmpty(originalValue))
            {
                Trace.Info($"No backup value was found. Deleting the value.");
                //there was no backup value present, just delete the current one
                _registryManager.DeleteValue(targetHive, subKeyName, name);
            }
            else
            {
                Trace.Info($"Backup value was found. Revert it to the original value.");
                //revert to the original value
                _registryManager.SetValue(targetHive, subKeyName, name, originalValue);
            }

            Trace.Info($"Deleting the backup key now.");
            //delete the value that we created for backup purpose
            _registryManager.DeleteValue(targetHive, subKeyName, nameofTheBackupValue);
        }

        private string GetBackupValueName(string valueName)
        {
            return string.Concat(RegistryConstants.BackupKeyPrefix, valueName);
        }

        private void ShowAutoLogonWarningIfAlreadyEnabled(string domainName, string userName)
        {
            //we cant use store here as store is specific to the agent root and if there is some other on the agent, we dont have access to it
            //we need to rely on the registry only
            GetAutoLogonUserDetails(out string autoLogonUserName, out string autoLogonUserDomainName);
            
            if (autoLogonUserName != null
                    && autoLogonUserDomainName != null
                    && !domainName.Equals(autoLogonUserDomainName, StringComparison.CurrentCultureIgnoreCase)
                    && !userName.Equals(autoLogonUserName, StringComparison.CurrentCultureIgnoreCase))
            {
                _terminal.WriteLine(StringUtil.Loc("AutoLogonAlreadyEnabledWarning", userName));
            }
        }

        private void GetAutoLogonUserDetails(out string userName, out string domainName)
        {
            userName = null;
            domainName = null;

            var regValue = _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon, 
                                                        RegistryConstants.MachineSettings.ValueNames.AutoLogon);
            if (int.TryParse(regValue, out int autoLogonEnabled)
                    && autoLogonEnabled == 1)
            {
                userName = _registryManager.GetValue(RegistryHive.LocalMachine,
                                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                                        RegistryConstants.MachineSettings.ValueNames.AutoLogonUserName);
                domainName = _registryManager.GetValue(RegistryHive.LocalMachine, 
                                                        RegistryConstants.MachineSettings.SubKeys.AutoLogon,
                                                        RegistryConstants.MachineSettings.ValueNames.AutoLogonDomainName);
            }
        }
    }

    public class RegistryConstants
    {
        public const string BackupKeyPrefix = "VSTSAgentBackup_";

        public class MachineSettings
        {
            public class SubKeys
            {
                public const string AutoLogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
                public const string ShutdownReasonDomainPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Reliability";
                public const string LegalNotice = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            }

            public class ValueNames
            {
                public const string AutoLogon = "AutoAdminLogon";
                public const string AutoLogonUserName = "DefaultUserName";
                public const string AutoLogonDomainName = "DefaultDomainName";
                public const string AutoLogonCount = "AutoLogonCount";
                public const string AutoLogonPassword = "DefaultPassword";

                public const string ShutdownReason = "ShutdownReasonOn";                
                public const string LegalNoticeCaption = "LegalNoticeCaption";
                public const string LegalNoticeText = "LegalNoticeText";
            }
        }

        public class UserSettings
        {
            public class SubKeys
            {
                public const string ScreenSaver = @"Control Panel\Desktop";
                public const string ScreenSaverDomainPolicy = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";
                public const string StartupProcess = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            }

            public class ValueNames
            {
                public const string ScreenSaver = "ScreenSaveActive";
                public const string StartupProcess = "VSTSAgent";
            }
        }
    }
}
#endif