#if OS_WINDOWS
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(AutoLogonRegistryManager))]
    public interface IAutoLogonRegistryManager : IAgentService
    {
        void LogWarnings(string domainName, string userName);
        void UpdateRegistrySettings(string domainName, string userName);
        bool DoesRegistryExistForUser(string domainName, string userName);
        void RevertRegistrySettings(string domainName, string userName);
    }

    public class AutoLogonRegistryManager : AgentService, IAutoLogonRegistryManager
    {
        private IWindowsRegistryManager _registryManager;
        private INativeWindowsServiceHelper _windowsServiceHelper;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _registryManager = hostContext.GetService<IWindowsRegistryManager>();
            _windowsServiceHelper = hostContext.GetService<INativeWindowsServiceHelper>();
        }

        public void LogWarnings(string domainName, string userName)
        {
            var warningReasons = GetWarningsForMachineSpecificSettings();
            warningReasons.AddRange(GetWarningsForUserSpecificSettings(domainName, userName));

            if (warningReasons.Count > 0)
            {
                var terminal = HostContext.GetService<ITerminal>();
                terminal.WriteLine();
                terminal.WriteLine(StringUtil.Loc("UITestingWarning"));
                for (int i=0; i < warningReasons.Count; i++)
                {
                    terminal.WriteLine(String.Format("{0} - {1}", i + 1, warningReasons[i]));
                }
                terminal.WriteLine();
            }
        }

        public void UpdateRegistrySettings(string domainName, string userName)
        {
            ShowAutoLogonWarningIfAlreadyEnabled(domainName, userName);

            //machine specific
            UpdateMachineSpecificRegistrySettings(domainName, userName);

            //user specific
            UpdateUserSpecificRegistrySettings(domainName, userName);
        }

        public void RevertRegistrySettings(string domainName, string userName)
        {
            //machine specific
            var hive = RegistryHive.LocalMachine;
            
            RevertOriginalValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonUserName);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonDomainName);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonPassword);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonCount);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogon);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.ShutdownReasonDomainPolicy, RegistryConstants.ValueNames.ShutdownReason);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.ShutdownReasonDomainPolicy, RegistryConstants.ValueNames.ShutdownReasonUI);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.LegalNotice, RegistryConstants.ValueNames.LegalNoticeCaption);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.LegalNotice, RegistryConstants.ValueNames.LegalNoticeText);

            //user specific            
            if (!_windowsServiceHelper.HasActiveSession(domainName, userName))
            {
                string securityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
                RevertRegistrySettingsForDifferentUser(securityId);
            }
            else
            {
                RevertRegistrySettingsForCurrentUser();
            }
        }

        public bool DoesRegistryExistForUser(string domainName, string userName)
        {
            if (!_windowsServiceHelper.HasActiveSession(domainName, userName))
            {
                string securityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
                return _registryManager.RegsitryExists(securityId);
            }
            return true;
        }

        private List<string> GetWarningsForUserSpecificSettings(string domainName, string userName)
        {
            string screenSaverValue = null;
            List<string> warningReasons = new List<string>();

            if (!_windowsServiceHelper.HasActiveSession(domainName, userName))
            {
                string securityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
                screenSaverValue = _registryManager.GetValue(RegistryHive.Users, $"{securityId}\\{RegistryConstants.SubKeys.ScreenSaverDomainPolicy}", RegistryConstants.ValueNames.ScreenSaver);
            }
            else
            {
                screenSaverValue = _registryManager.GetValue(RegistryHive.CurrentUser, RegistryConstants.SubKeys.ScreenSaverDomainPolicy, RegistryConstants.ValueNames.ScreenSaver);
            }

            if (int.TryParse(screenSaverValue, out int isScreenSaverDomainPolicySet)
                    && isScreenSaverDomainPolicySet == 1)
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_ScreenSaver"));
            }

            return warningReasons;
        }

        private List<string> GetWarningsForMachineSpecificSettings()
        {
            var warningReasons = new List<string>();

            //shutdown reason
            var shutdownReasonValue = _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.ShutdownReasonDomainPolicy, RegistryConstants.ValueNames.ShutdownReason);
            if (int.TryParse(shutdownReasonValue, out int shutdownReasonOn) 
                    && shutdownReasonOn == 1)
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_ShutdownReason"));
            }

            //legal caption/text
            var legalNoticeCaption = _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.LegalNotice, RegistryConstants.ValueNames.LegalNoticeCaption);
            var legalNoticeText =  _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.LegalNotice, RegistryConstants.ValueNames.LegalNoticeText);
            if (!string.IsNullOrEmpty(legalNoticeCaption) || !string.IsNullOrEmpty(legalNoticeText))
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_LegalNotice"));
            }

            //auto-logon
            var autoLogonCountValue = _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogon);
            if (!string.IsNullOrEmpty(autoLogonCountValue))
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_AutoLogonCount"));
            }           
            
            return warningReasons;
        }

        private void UpdateMachineSpecificRegistrySettings(string domainName, string userName)
        {
            var hive = RegistryHive.LocalMachine;

            // SetValue(RegistryHive targetHive, string subKeyName, string name, string value)
            SetValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonUserName, userName);
            SetValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonDomainName, domainName);

            //this call is to take the backup of the password key if already exists as we delete the key in the next step
            SetValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonPassword, "");
            _registryManager.DeleteValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonPassword);
            
            SetValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonCount, "");
            _registryManager.DeleteValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonCount);

            SetValue(hive, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogon, "1");

            SetValue(hive, RegistryConstants.SubKeys.ShutdownReasonDomainPolicy, RegistryConstants.ValueNames.ShutdownReason, "0");
            SetValue(hive, RegistryConstants.SubKeys.ShutdownReasonDomainPolicy, RegistryConstants.ValueNames.ShutdownReasonUI, "0");

            SetValue(hive, RegistryConstants.SubKeys.LegalNotice, RegistryConstants.ValueNames.LegalNoticeCaption, "");
            SetValue(hive, RegistryConstants.SubKeys.LegalNotice, RegistryConstants.ValueNames.LegalNoticeText, "");
        }

        private void UpdateUserSpecificRegistrySettings(string domainName, string userName)
        {
            if (!_windowsServiceHelper.HasActiveSession(domainName, userName))
            {
                string securityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
                UpdateRegistrySettingsForDifferentUser(securityId);                
            }
            else
            {
                UpdateRegistrySettingsForCurrentUser();
            }
        }

        private void UpdateRegistrySettingsForCurrentUser()
        {
            var hive = RegistryHive.CurrentUser;
            SetValue(hive, RegistryConstants.SubKeys.ScreenSaver, RegistryConstants.ValueNames.ScreenSaver, "0");
            SetValue(hive, RegistryConstants.SubKeys.ScreenSaverDomainPolicy, RegistryConstants.ValueNames.ScreenSaver, "0");
            SetValue(hive, RegistryConstants.SubKeys.StartupProcess, RegistryConstants.ValueNames.StartupProcess, GetStartupCommand());
        }

        private void UpdateRegistrySettingsForDifferentUser(string securityId)
        {
            var hive = RegistryHive.Users;
            SetValue(hive, $"{securityId}\\{RegistryConstants.SubKeys.ScreenSaver}", RegistryConstants.ValueNames.ScreenSaver, "0");
            SetValue(hive, $"{securityId}\\{RegistryConstants.SubKeys.ScreenSaverDomainPolicy}", RegistryConstants.ValueNames.ScreenSaver, "0");
            SetValue(hive, $"{securityId}\\{RegistryConstants.SubKeys.StartupProcess}", RegistryConstants.ValueNames.StartupProcess, GetStartupCommand());
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
            
            //extra "" are to handle the spaces in the file path (if any)
            var startupCommand = $@"start ""Agent with AutoLogon"" {cmdExePath} /D /S /C """"{filePath}""""";
            
            Trace.Info($"Setting startup command as '{startupCommand}'");
            return startupCommand;
        }  

        private void RevertOriginalValue(RegistryHive targetHive, string subKeyName, string name)
        {
            var nameofTheBackupValue = GetBackupValueName(name);
            var originalValue = _registryManager.GetValue(targetHive, subKeyName, nameofTheBackupValue);

            if (string.IsNullOrEmpty(originalValue))
            {
                //there was no backup value present, just delete the current one
                _registryManager.DeleteValue(targetHive, subKeyName, name);
            }
            else
            {
                //revert to the original value
                _registryManager.SetValue(targetHive, subKeyName, name, originalValue);
            }

            //delete the value that we created for backup purpose
            _registryManager.DeleteValue(targetHive, subKeyName, nameofTheBackupValue);
        }

        private void RevertRegistrySettingsForCurrentUser()
        {
            var hive = RegistryHive.CurrentUser;
            RevertOriginalValue(hive, RegistryConstants.SubKeys.ScreenSaver, RegistryConstants.ValueNames.ScreenSaver);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.ScreenSaverDomainPolicy, RegistryConstants.ValueNames.ScreenSaver);
            RevertOriginalValue(hive, RegistryConstants.SubKeys.StartupProcess, RegistryConstants.ValueNames.StartupProcess);
        }

        private void RevertRegistrySettingsForDifferentUser(string securityId)
        {
            var hive = RegistryHive.Users;
            RevertOriginalValue(hive, $"{securityId}\\{RegistryConstants.SubKeys.ScreenSaver}", RegistryConstants.ValueNames.ScreenSaver);
            RevertOriginalValue(hive, $"{securityId}\\{RegistryConstants.SubKeys.ScreenSaverDomainPolicy}", RegistryConstants.ValueNames.ScreenSaver);
            RevertOriginalValue(hive, $"{securityId}\\{RegistryConstants.SubKeys.StartupProcess}", RegistryConstants.ValueNames.StartupProcess);
        }

        private void SetValue(RegistryHive targetHive, string subKeyName, string name, string value)
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

        private string GetBackupValueName(string valueName)
        {
            return string.Concat(RegistryConstants.BackupKeyPrefix, valueName);
        }

        private void ShowAutoLogonWarningIfAlreadyEnabled(string domainName, string userName)
        {
            //we cant use store here as store is specific to the agent root and if there is some other on the agent, we dont have access to it
            //we need to rely on the registry only
            FetchAutoLogonUserDetails(out string autoLogonUserName, out string autoLogonUserDomainName);
            
            if (autoLogonUserName != null
                    && autoLogonUserDomainName != null
                    && !domainName.Equals(autoLogonUserDomainName, StringComparison.CurrentCultureIgnoreCase)
                    && !userName.Equals(autoLogonUserName, StringComparison.CurrentCultureIgnoreCase))
            {
                var terminal = HostContext.GetService<ITerminal>();
                terminal.WriteLine(StringUtil.Loc("AutoLogonAlreadyEnabledWarning", userName));
            }
        }

        private void FetchAutoLogonUserDetails(out string userName, out string domainName)
        {
            userName = null;
            domainName = null;

            var regValue = _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogon);
            if (int.TryParse(regValue, out int autoLogonEnabled)
                    && autoLogonEnabled == 1)
            {
                userName = _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonUserName);
                domainName = _registryManager.GetValue(RegistryHive.LocalMachine, RegistryConstants.SubKeys.AutoLogon, RegistryConstants.ValueNames.AutoLogonDomainName);
            }
        }
    }

    public class RegistryConstants
    {
        public const string BackupKeyPrefix = "VSTSAgentBackup_";

        public struct SubKeys
        {
            public const string ScreenSaver = @"Control Panel\Desktop";
            public const string ScreenSaverDomainPolicy = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";
            public const string StartupProcess = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            public const string AutoLogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            public const string ShutdownReasonDomainPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Reliability";
            public const string LegalNotice = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        }

        public struct ValueNames
        {
            public const string ScreenSaver = "ScreenSaveActive";
            public const string AutoLogon = "AutoAdminLogon";
            public const string AutoLogonUserName = "DefaultUserName";
            public const string AutoLogonDomainName = "DefaultDomainName";
            public const string AutoLogonCount = "AutoLogonCount";
            public const string AutoLogonPassword = "DefaultPassword";
            public const string StartupProcess = "VSTSAgent";
            public const string ShutdownReason = "ShutdownReasonOn";
            public const string ShutdownReasonUI = "ShutdownReasonUI";
            public const string LegalNoticeCaption = "LegalNoticeCaption";
            public const string LegalNoticeText = "LegalNoticeText";
        }
    }
}
#endif