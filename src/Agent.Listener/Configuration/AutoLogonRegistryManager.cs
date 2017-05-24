#if OS_WINDOWS
using System;
using System.Collections.Generic;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(AutoLogonRegistryManager))]
    public interface IAutoLogonRegistryManager : IAgentService
    {
        void UpdateAutoLogonSettings(string userName, string domainName);
        void FetchAutoLogonUserDetails(out string userName, out string domainName);
        bool DoesRegistryExistForUser(string userSecurityId);
        void UpdateStandardRegistrySettings(string userSecurityId);
        void RevertOriginalRegistrySettings(string userSecurityId);
        void SetStartupProcessCommand(string userSecurityId, string startupCommand);
        string GetStartupProcessCommand(string userSecurityId);
        List<string> GetAutoLogonRelatedWarningsIfAny(string userSecurityId);        
    }

    public class AutoLogonRegistryManager : AgentService, IAutoLogonRegistryManager
    {
        private IWindowsRegistryManager _registryManager;
        private List<KeyValuePair<WellKnownRegistries, string>> _standardRegistries;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _registryManager = hostContext.GetService<IWindowsRegistryManager>();
            InitializeStandardRegistrySettings();
        }

        public bool DoesRegistryExistForUser(string userSecurityId)
        {
            return _registryManager.RegsitryExists(userSecurityId);
        }

        public void UpdateStandardRegistrySettings(string userSecurityId)
        {
            foreach (var regSetting in _standardRegistries)
            {
                SetRegistryKeyValue(regSetting.Key, regSetting.Value, userSecurityId);
            }
        }

        public void RevertOriginalRegistrySettings(string userSecurityId)
        {
            foreach (var regSetting in _standardRegistries)
            {
                RevertOriginalRegistry(regSetting.Key, userSecurityId);
            }

            //auto-logon
            RevertOriginalRegistry(WellKnownRegistries.AutoLogonUserName, userSecurityId);
            RevertOriginalRegistry(WellKnownRegistries.AutoLogonDomainName, userSecurityId);
            RevertOriginalRegistry(WellKnownRegistries.AutoLogonPassword, userSecurityId);
            RevertOriginalRegistry(WellKnownRegistries.AutoLogonCount, userSecurityId);
            RevertOriginalRegistry(WellKnownRegistries.AutoLogon, userSecurityId);

            //startup process
            RevertOriginalRegistry(WellKnownRegistries.StartupProcess, userSecurityId);
        }

        public void UpdateAutoLogonSettings(string userName, string domainName)
        {
            SetRegistryKeyValue(WellKnownRegistries.AutoLogonUserName, userName);
            SetRegistryKeyValue(WellKnownRegistries.AutoLogonDomainName, domainName);

            //this call is to take the backup of the password key if already exists as we delete the key in the next step
            SetRegistryKeyValue(WellKnownRegistries.AutoLogonPassword, "");
            DeleteRegistry(WellKnownRegistries.AutoLogonPassword);

            //this call is to take the backup of the password key if already exists as we delete the key in the next step
            SetRegistryKeyValue(WellKnownRegistries.AutoLogonCount, "");
            DeleteRegistry(WellKnownRegistries.AutoLogonCount);

            SetRegistryKeyValue(WellKnownRegistries.AutoLogon, "1");
        }

        public void SetStartupProcessCommand(string userSecurityId, string startupCommand)
        {
            SetRegistryKeyValue(WellKnownRegistries.StartupProcess, startupCommand, userSecurityId);
        }

        public string GetStartupProcessCommand(string userSecurityId)
        {
            return GetRegistryKeyValue(WellKnownRegistries.StartupProcess, userSecurityId);
        }

        public List<string> GetAutoLogonRelatedWarningsIfAny(string userSecurityId)
        {
            var warningReasons = new List<string>();

            //screen saver
            var screenSaverValue = GetRegistryKeyValue(WellKnownRegistries.ScreenSaverDomainPolicy, userSecurityId);
            if (int.TryParse(screenSaverValue, out int isScreenSaverDomainPolicySet)
                    && isScreenSaverDomainPolicySet == 1)
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_ScreenSaver"));
            }

            //shutdown reason
            var shutdownReasonValue = GetRegistryKeyValue(WellKnownRegistries.ShutdownReason, userSecurityId);
            ;
            if (int.TryParse(shutdownReasonValue, out int shutdownReasonOn) 
                    && shutdownReasonOn == 1)
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_ShutdownReason"));
            }

            //legal caption/text
            var legalNoticeCaption = GetRegistryKeyValue(WellKnownRegistries.LegalNoticeCaption, userSecurityId);
            var legalNoticeText =  GetRegistryKeyValue(WellKnownRegistries.LegalNoticeText, userSecurityId);
            if (!string.IsNullOrEmpty(legalNoticeCaption) || !string.IsNullOrEmpty(legalNoticeText))
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_LegalNotice"));
            }

            //auto-logon
            var autoLogonCountValue = GetRegistryKeyValue(WellKnownRegistries.AutoLogonCount, userSecurityId);
            if (!string.IsNullOrEmpty(autoLogonCountValue))
            {
                warningReasons.Add(StringUtil.Loc("UITestingWarning_AutoLogonCount"));
            }
            
            return warningReasons;
        }

        public void FetchAutoLogonUserDetails(out string userName, out string domainName)
        {
            userName = null;
            domainName = null;

            var regValue = GetRegistryKeyValue(WellKnownRegistries.AutoLogon);
            if (int.TryParse(regValue, out int autoLogonEnabled)
                    && autoLogonEnabled == 1)
            {
                userName = GetRegistryKeyValue(WellKnownRegistries.AutoLogonUserName);
                domainName = GetRegistryKeyValue(WellKnownRegistries.AutoLogonDomainName);
            }
        }       

        private void SetRegistryKeyValue(WellKnownRegistries targetRegistry, string keyValue, string userSecurityId = null)
        {
            TakeBackupIfNeeded(targetRegistry, userSecurityId);
            SetRegistryKeyInternal(targetRegistry, keyValue, userSecurityId);
        }

        private string GetRegistryKeyValue(WellKnownRegistries targetRegistry, string userSecurityId = null)
        {
            var regPath = GetRegistryKeyPath(targetRegistry, userSecurityId);
            if (string.IsNullOrEmpty(regPath))
            {
                return null;
            }
            
            var regKey = RegistryConstants.GetActualKeyNameForWellKnownRegistry(targetRegistry);
            return _registryManager.GetKeyValue(regPath, regKey);
        }

        private void DeleteRegistry(WellKnownRegistries targetRegistry, bool deleteBackupKey = false, string userSecurityId = null)
        {
            var regKeyName = deleteBackupKey 
                                ? RegistryConstants.GetBackupKeyName(targetRegistry) 
                                : RegistryConstants.GetActualKeyNameForWellKnownRegistry(targetRegistry);

            /* .net Registry class works in following way based on the hive
            LocalMachine -> Open subkey from Registry.LocalMachine and the subkey path should not be having HKLM in it.
            CurrentUser  -> Open subkey from Registry.CurrentUser and the subkey path should not be having HKLM in it.
            Different user -> Open subkey from Registry.Users and the subkey path should start with the security ID of the user followed by the original path.
            Once you have opened the subkey you can call .DeleteValue() by suplying the key name.
             */
            string regPathTemplate = string.IsNullOrEmpty(userSecurityId)
                                        ? "{0}"
                                        : $@"{userSecurityId}\{{0}}";
                                        
            var regScope = string.IsNullOrEmpty(userSecurityId) ? RegistryScope.CurrentUser : RegistryScope.DifferentUser;

            switch (targetRegistry)
            {
                //user specific registry settings
                case WellKnownRegistries.ScreenSaver :
                    _registryManager.DeleteKey(regScope, string.Format(regPathTemplate, RegistryConstants.RegPaths.ScreenSaver), regKeyName);
                    break;
                case WellKnownRegistries.ScreenSaverDomainPolicy :
                    _registryManager.DeleteKey(regScope, string.Format(regPathTemplate, RegistryConstants.RegPaths.ScreenSaverDomainPolicy), regKeyName);
                    break;
                case WellKnownRegistries.StartupProcess:
                    _registryManager.DeleteKey(regScope, string.Format(regPathTemplate, RegistryConstants.RegPaths.StartupProcess), regKeyName);
                    break;

                //machine specific registry settings
                case WellKnownRegistries.AutoLogon :
                case WellKnownRegistries.AutoLogonUserName :
                case WellKnownRegistries.AutoLogonDomainName :
                case WellKnownRegistries.AutoLogonPassword :
                case WellKnownRegistries.AutoLogonCount :
                    _registryManager.DeleteKey(RegistryScope.LocalMachine, RegistryConstants.RegPaths.AutoLogon, regKeyName);
                    break;

                case WellKnownRegistries.ShutdownReason :
                case WellKnownRegistries.ShutdownReasonUI :
                    _registryManager.DeleteKey(RegistryScope.LocalMachine, RegistryConstants.RegPaths.ShutdownReasonDomainPolicy, regKeyName);
                    break;
                case WellKnownRegistries.LegalNoticeCaption :
                case WellKnownRegistries.LegalNoticeText :
                    _registryManager.DeleteKey(RegistryScope.LocalMachine, RegistryConstants.RegPaths.LegalNotice, regKeyName);
                    break;
                default:
                   throw new InvalidOperationException(StringUtil.Loc("InvalidRegKey"));
            }
        }

        private void RevertOriginalRegistry(WellKnownRegistries targetRegistry, string userSecurityId = null)
        {
            var regPath = GetRegistryKeyPath(targetRegistry, userSecurityId);
            RevertOriginalRegistryInternal(regPath, targetRegistry, userSecurityId);
        }

        private string GetRegistryKeyPath(WellKnownRegistries targetRegistry, string userSid = null)
        {
            var userHivePath = GetUserRegistryRootPath(userSid);
            switch (targetRegistry)
            {
                //user specific registry settings
                case WellKnownRegistries.ScreenSaver :
                    return string.Format($@"{userHivePath}\{RegistryConstants.RegPaths.ScreenSaver}");

                case WellKnownRegistries.ScreenSaverDomainPolicy:
                    return string.Format($@"{userHivePath}\{RegistryConstants.RegPaths.ScreenSaverDomainPolicy}");

                case WellKnownRegistries.StartupProcess:
                    return string.Format($@"{userHivePath}\{RegistryConstants.RegPaths.StartupProcess}");

                //machine specific registry settings         
                case WellKnownRegistries.AutoLogon :
                case WellKnownRegistries.AutoLogonUserName:
                case WellKnownRegistries.AutoLogonDomainName :
                case WellKnownRegistries.AutoLogonPassword:
                case WellKnownRegistries.AutoLogonCount:
                    return string.Format($@"{RegistryConstants.LocalMachineRootPath}\{RegistryConstants.RegPaths.AutoLogon}");

                case WellKnownRegistries.ShutdownReason :
                case WellKnownRegistries.ShutdownReasonUI :
                    return string.Format($@"{RegistryConstants.LocalMachineRootPath}\{RegistryConstants.RegPaths.ShutdownReasonDomainPolicy}");

                case WellKnownRegistries.LegalNoticeCaption :
                case WellKnownRegistries.LegalNoticeText :
                    return string.Format($@"{RegistryConstants.LocalMachineRootPath}\{RegistryConstants.RegPaths.LegalNotice}");
                default:
                   return null;
            }
        }

        private void RevertOriginalRegistryInternal(string regPath, WellKnownRegistries targetRegistry, string userSecurityId)
        {
            var originalKeyName = RegistryConstants.GetActualKeyNameForWellKnownRegistry(targetRegistry);
            var backupKeyName = RegistryConstants.GetBackupKeyName(targetRegistry);

            var originalValue = _registryManager.GetKeyValue(regPath, backupKeyName);            
            if (string.IsNullOrEmpty(originalValue))
            {
                DeleteRegistry(targetRegistry: targetRegistry, deleteBackupKey: false, userSecurityId: userSecurityId);
                return;
            }

            //revert the original value
            _registryManager.SetKeyValue(regPath, originalKeyName, originalValue);
            //delete the backup key
            DeleteRegistry(targetRegistry: targetRegistry, deleteBackupKey: true, userSecurityId: userSecurityId);
        }
        
        private void SetRegistryKeyInternal(WellKnownRegistries targetRegistry, string keyValue, string userSecurityId)
        {
            var regPath = GetRegistryKeyPath(targetRegistry, userSecurityId);
            var regKeyName = RegistryConstants.GetActualKeyNameForWellKnownRegistry(targetRegistry);
            _registryManager.SetKeyValue(regPath, regKeyName, keyValue);
        }

        private string GetUserRegistryRootPath(string sid)
        {
            return string.IsNullOrEmpty(sid) ?
                RegistryConstants.CurrentUserRootPath :
                String.Format(RegistryConstants.DifferentUserRootPath, sid);
        }

        private void TakeBackupIfNeeded(WellKnownRegistries registry, string userSecurityId)
        {
            string origValue = GetRegistryKeyValue(registry, userSecurityId);
            if (!string.IsNullOrEmpty(origValue))
            {
                var regPath = GetRegistryKeyPath(registry, userSecurityId);
                var backupKeyName = RegistryConstants.GetBackupKeyName(registry);
                _registryManager.SetKeyValue(regPath, backupKeyName, origValue);
            }
        }

        private void InitializeStandardRegistrySettings()
        {
            _standardRegistries = new List<KeyValuePair<WellKnownRegistries, string>>()
            {
                new KeyValuePair<WellKnownRegistries, string>(WellKnownRegistries.ScreenSaver, "0"),
                new KeyValuePair<WellKnownRegistries, string>(WellKnownRegistries.ScreenSaverDomainPolicy, "0"),
                new KeyValuePair<WellKnownRegistries, string>(WellKnownRegistries.ShutdownReason, "0"),
                new KeyValuePair<WellKnownRegistries, string>(WellKnownRegistries.ShutdownReasonUI, "0"),
                new KeyValuePair<WellKnownRegistries, string>(WellKnownRegistries.LegalNoticeCaption, ""),
                new KeyValuePair<WellKnownRegistries, string>(WellKnownRegistries.LegalNoticeText, "")
            };
        }    
    }
    
    public enum WellKnownRegistries
    {
        ScreenSaver,
        ScreenSaverDomainPolicy,
        AutoLogon,
        AutoLogonUserName,
        AutoLogonDomainName,
        AutoLogonCount,
        AutoLogonPassword,
        StartupProcess,
        ShutdownReason,
        ShutdownReasonUI,
        LegalNoticeCaption,
        LegalNoticeText
    }

    public enum RegistryScope
    {
        CurrentUser,
        DifferentUser,
        LocalMachine
    }

    public class RegistryConstants
    {
        public const string CurrentUserRootPath = @"HKEY_CURRENT_USER";
        public const string LocalMachineRootPath = @"HKEY_LOCAL_MACHINE";
        public const string DifferentUserRootPath = @"HKEY_USERS\{0}";
        public const string BackupKeyPrefix = "VSTSAgentBackup_";

        public struct RegPaths
        {
            public const string ScreenSaver = @"Control Panel\Desktop";
            public const string ScreenSaverDomainPolicy = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";
            public const string StartupProcess = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            public const string AutoLogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            public const string ShutdownReasonDomainPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Reliability";
            public const string LegalNotice = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        }

        public struct KeyNames
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

        public static string GetActualKeyNameForWellKnownRegistry(WellKnownRegistries registry)
        {
            switch (registry)
            {
                case WellKnownRegistries.ScreenSaverDomainPolicy:
                case WellKnownRegistries.ScreenSaver:
                    return KeyNames.ScreenSaver;
                case WellKnownRegistries.AutoLogon:
                    return KeyNames.AutoLogon;
                case WellKnownRegistries.AutoLogonUserName :
                    return KeyNames.AutoLogonUserName;
                case WellKnownRegistries.AutoLogonDomainName:
                    return KeyNames.AutoLogonDomainName;
                case WellKnownRegistries.AutoLogonCount:
                    return KeyNames.AutoLogonCount;
                case WellKnownRegistries.AutoLogonPassword:
                    return KeyNames.AutoLogonPassword;
                case WellKnownRegistries.StartupProcess:
                    return KeyNames.StartupProcess;
                case WellKnownRegistries.ShutdownReason:
                    return KeyNames.ShutdownReason;
                case WellKnownRegistries.ShutdownReasonUI:
                    return KeyNames.ShutdownReasonUI;
                case WellKnownRegistries.LegalNoticeCaption:
                    return KeyNames.LegalNoticeCaption;
                case WellKnownRegistries.LegalNoticeText:
                    return KeyNames.LegalNoticeText;
                default:
                    return null;
            }                
        }

        public static string GetBackupKeyName(WellKnownRegistries registry)
        {
            return string.Concat(RegistryConstants.BackupKeyPrefix, GetActualKeyNameForWellKnownRegistry(registry));
        }
    }
}
#endif