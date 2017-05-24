#if OS_WINDOWS
using Microsoft.VisualStudio.Services.Agent;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(AutoLogonConfigurationManager))]
    public interface IAutoLogonConfigurationManager : IAgentService
    {
        void Configure(CommandSettings command);
        void Unconfigure();
        bool RestartNeeded();
    }

    public class AutoLogonConfigurationManager : AgentService, IAutoLogonConfigurationManager
    {
        private ITerminal _terminal;
        private INativeWindowsServiceHelper _windowsServiceHelper;
        private IAutoLogonRegistryManager _autoLogonRegManager;
        private IConfigurationStore _store;
        private string _userSecurityId;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _terminal = hostContext.GetService<ITerminal>();
            _windowsServiceHelper = hostContext.GetService<INativeWindowsServiceHelper>();
            _autoLogonRegManager = HostContext.GetService<IAutoLogonRegistryManager>();
            _store = hostContext.GetService<IConfigurationStore>();
            _userSecurityId = null;
        }

        public void Configure(CommandSettings command)
        {
            if (!_windowsServiceHelper.IsRunningInElevatedMode())
            {
                Trace.Error("Needs Administrator privileges to configure agent with AutoLogon capability.");
                Trace.Error("You will need to unconfigure the agent and then re-configure with Administrative rights");            
                throw new SecurityException(StringUtil.Loc("NeedAdminForAutologonCapability"));
            }

            string domainName;
            string userName;
            string logonAccount;
            string logonPassword;

            while (true)
            {   
                logonAccount = command.GetWindowsLogonAccount(defaultValue: string.Empty, descriptionMsg: StringUtil.Loc("AutoLogonAccountNameDescription"));
                GetAccountSegments(logonAccount, out domainName, out userName);

                if ((string.IsNullOrEmpty(domainName) || domainName.Equals(".", StringComparison.CurrentCultureIgnoreCase)) && !logonAccount.Contains("@"))
                {
                    logonAccount = String.Format("{0}\\{1}", Environment.MachineName, userName);
                }
                Trace.Info("LogonAccount after transforming: {0}, user: {1}, domain: {2}", logonAccount, userName, domainName);

                logonPassword = command.GetWindowsLogonPassword(logonAccount);
                if (_windowsServiceHelper.IsValidCredential(domainName, userName, logonPassword))
                {
                    Trace.Info("Credential validation succeeded");
                    break;
                }
                
                if (command.Unattended)
                {
                    throw new SecurityException(StringUtil.Loc("InvalidWindowsCredential"));
                }
                    
                Trace.Error("Invalid credential entered");
                _terminal.WriteLine(StringUtil.Loc("InvalidWindowsCredential"));
            }

            bool isCurrentUserSameAsAutoLogonUser = _windowsServiceHelper.HasActiveSession(domainName, userName);            
            if(!isCurrentUserSameAsAutoLogonUser)
            {
                _userSecurityId = _windowsServiceHelper.GetSecurityId(domainName, userName);
                if (!_autoLogonRegManager.DoesRegistryExistForUser(_userSecurityId))
                {
                    Trace.Error($"The autologon user '{logonAccount}' doesnt have a user profile on the machine. Please login once with the expected autologon user and reconfigure the agent again");
                    throw new InvalidOperationException(StringUtil.Loc("NoUserProfile", logonAccount));
                }
            }

            DisplayWarningsIfAny();        
            UpdateRegistriesForAutoLogon(userName, domainName, logonPassword);            
            ConfigurePowerOptions();
            SaveAutoLogonSettings(domainName, userName);
        }

        public bool RestartNeeded()
        {
            if (!_store.IsAutoLogonConfigured())
            {
                return false;
            }

            var autoLogonSettings = _store.GetAutoLogonSettings();
            return !_windowsServiceHelper.HasActiveSession(autoLogonSettings.UserDomainName, autoLogonSettings.UserName);
        }

        public void Unconfigure()
        {
            if (!_windowsServiceHelper.IsRunningInElevatedMode())
            {
                Trace.Error("Needs Administrator privileges to unconfigure an agent running with AutoLogon capability.");          
                throw new SecurityException(StringUtil.Loc("NeedAdminForAutologonRemoval"));
            }
            
            /* We need to find out first if the AutoLogon was configured for the same user
            If it is different user we should be reverting the AutoLogon user specific registries
            */            
            var autoLogonSettings = _store.GetAutoLogonSettings();
            if (!_windowsServiceHelper.HasActiveSession(autoLogonSettings.UserDomainName, autoLogonSettings.UserName))
            {
                _userSecurityId = _windowsServiceHelper.GetSecurityId(autoLogonSettings.UserDomainName, autoLogonSettings.UserName);
                Trace.Info($"AutoLogon is enabled for the different user {autoLogonSettings.UserDomainName}\\{autoLogonSettings.UserName}, reverting the registry settings now.");
            }
            else
            {
                Trace.Info($"AutoLogon is enabled for the same user, reverting the registry settings now.");
            }

            _autoLogonRegManager.RevertOriginalRegistrySettings(_userSecurityId);           

            Trace.Info("Deleting the autologon settings now.");
            _store.DeleteAutoLogonSettings();
            Trace.Info("Successfully deleted the autologon settings.");
        }

        private void SaveAutoLogonSettings(string domainName, string userName)
        {
            Trace.Entering();
            var settings = new AutoLogonSettings()
            {
                UserDomainName = domainName,
                UserName = userName
            };            
            _store.SaveAutoLogonSettings(settings);
            Trace.Info("Saved the autologon settings");
        }

        private void UpdateRegistriesForAutoLogon(string userName, string domainName, string logonPassword)
        {
            _autoLogonRegManager.UpdateStandardRegistrySettings(_userSecurityId);

            //autologon
            ConfigureAutoLogon(userName, domainName, logonPassword);

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

            _autoLogonRegManager.SetStartupProcessCommand(_userSecurityId, startupCommand);
        }

        private void ConfigureAutoLogon(string userName, string domainName, string logonPassword)
        {
            //find out if the autologon was already enabled, show warning in that case
            ShowAutoLogonWarningIfAlreadyEnabled(userName, domainName);
            _windowsServiceHelper.SetAutoLogonPassword(logonPassword);
            _autoLogonRegManager.UpdateAutoLogonSettings(userName, domainName);
        }

        private void ShowAutoLogonWarningIfAlreadyEnabled(string userName, string domainName)
        {
            //we cant use store here as store is specific to the agent root and if there is some other on the agent, we dont have access to it
            //we need to rely on the registry only
            _autoLogonRegManager.FetchAutoLogonUserDetails(out string autoLogonUserName, out string autoLogonUserDomainName);
            if (autoLogonUserName != null
                    && autoLogonUserDomainName != null
                    && !domainName.Equals(autoLogonUserDomainName, StringComparison.CurrentCultureIgnoreCase)
                    && !userName.Equals(autoLogonUserName, StringComparison.CurrentCultureIgnoreCase))
            {
                _terminal.WriteLine(StringUtil.Loc("AutoLogonAlreadyEnabledWarning", userName));
            }
        }

        private void DisplayWarningsIfAny()
        {
            var warningReasons = _autoLogonRegManager.GetAutoLogonRelatedWarningsIfAny(_userSecurityId);
            if (warningReasons.Count > 0)
            {
                _terminal.WriteLine();
                _terminal.WriteLine(StringUtil.Loc("UITestingWarning"));
                for (int i=0; i < warningReasons.Count; i++)
                {
                    _terminal.WriteLine(String.Format("{0} - {1}", i + 1, warningReasons[i]));
                }
                _terminal.WriteLine();
            }
        }

        private async Task ConfigurePowerOptions()
        {
            var whichUtil = HostContext.GetService<IWhichUtil>();
            var filePath = whichUtil.Which("powercfg.exe", require:true);
            string[] commands = new string[] {"/Change monitor-timeout-ac 0", "/Change monitor-timeout-dc 0"};

            foreach (var command in commands)
            {
                try
                {
                    Trace.Info($"Running powercfg.exe with {command}");
                    using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
                    {
                        processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                        {
                            Trace.Info(message.Data);
                        };

                        processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                        {
                            Trace.Error(message.Data);
                        };

                        await processInvoker.ExecuteAsync(
                                workingDirectory: string.Empty,
                                fileName: filePath,
                                arguments: command,
                                environment: null,
                                cancellationToken: CancellationToken.None);
                    }
                }
                catch(Exception ex)
                {
                    //we will not stop the configuration. just show the warning and continue
                    _terminal.WriteError(StringUtil.Loc("PowerOptionsConfigError"));
                    Trace.Error(ex);
                }
            }
        }

        //todo: move it to a utility class so that at other places it can be re-used
        private void GetAccountSegments(string account, out string domain, out string user)
        {
            string[] segments = account.Split('\\');
            domain = string.Empty;
            user = account;
            if (segments.Length == 2)
            {
                domain = segments[0];
                user = segments[1];
            }
        }
    }
}
#endif