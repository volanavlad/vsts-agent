#if OS_WINDOWS
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(AutoLogonConfigurationManager))]
    public interface IAutoLogonConfigurationManager : IAgentService
    {
        void Configure(CommandSettings command);
        void Unconfigure();
        bool RestartNeeded();
        bool IsAutoLogonConfigured();
    }

    public class AutoLogonConfigurationManager : AgentService, IAutoLogonConfigurationManager
    {
        private ITerminal _terminal;
        private INativeWindowsServiceHelper _windowsServiceHelper;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _terminal = hostContext.GetService<ITerminal>();
            _windowsServiceHelper = hostContext.GetService<INativeWindowsServiceHelper>();
        }

        public void Configure(CommandSettings command)
        {
            if(!_windowsServiceHelper.IsRunningInElevatedMode())
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
                logonAccount = command.GetAutoLogonUserName();
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
            var securityIdForTheUser = _windowsServiceHelper.GetSecurityId(domainName, userName);
            var regManager = HostContext.GetService<IWindowsRegistryManager>();
            
            AutoLogonRegistryManager regHelper = isCurrentUserSameAsAutoLogonUser 
                                                ? new AutoLogonRegistryManager(regManager)
                                                : new AutoLogonRegistryManager(regManager, securityIdForTheUser);
            
            if (!isCurrentUserSameAsAutoLogonUser && !regHelper.DoesRegistryExistForUser(securityIdForTheUser))
            {
                Trace.Error($"The autologon user '{logonAccount}' doesnt have a user profile on the machine. Please login once with the expected autologon user and reconfigure the agent again");
                throw new InvalidOperationException(StringUtil.Loc("NoUserProfile", logonAccount));
            }

            DisplayWarningsIfAny(regHelper);        
            UpdateRegistriesForAutoLogon(regHelper, userName, domainName, logonPassword);
            ConfigurePowerOptions();
        }

        public bool RestartNeeded()
        {
            var regHelper = new AutoLogonRegistryManager(HostContext.GetService<IWindowsRegistryManager>());            
            regHelper.FetchAutoLogonUserDetails(out string userName, out string domainName);
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(domainName))
            {
                throw new InvalidOperationException(StringUtil.Loc("AutoLogonNotConfigured"));
            }

            return !_windowsServiceHelper.HasActiveSession(domainName, userName);
        }

        public void Unconfigure()
        {
            if(!_windowsServiceHelper.IsRunningInElevatedMode())
            {
                Trace.Error("Needs Administrator privileges to unconfigure an agent running with AutoLogon capability.");          
                throw new SecurityException(StringUtil.Loc("NeedAdminForAutologonRemoval"));
            }
            
            /* We need to find out first if the AutoLogon was configured for the same user
            If it is different user we should be reverting the AutoLogon user specific registries
            */
            var regManager = HostContext.GetService<IWindowsRegistryManager>();
            var regHelper = new AutoLogonRegistryManager(regManager);
            regHelper.FetchAutoLogonUserDetails(out string userName, out string domainName);
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(domainName))
            {
                throw new InvalidOperationException(StringUtil.Loc("AutoLogonNotConfigured"));
            }

            if (_windowsServiceHelper.HasActiveSession(domainName, userName))
            {
                Trace.Info($"AutoLogon is enabled for the same user, reverting the registry settings now.");
                regHelper.RevertOriginalRegistrySettings();
            }
            else
            {
                Trace.Info($"AutoLogon is enabled for the different user {domainName}\\{userName}, reverting the registry settings now.");
                var securityIdForTheUser = _windowsServiceHelper.GetSecurityId(domainName, userName);
                AutoLogonRegistryManager regHelperForDiffUser = new AutoLogonRegistryManager(regManager, securityIdForTheUser);
                regHelperForDiffUser.RevertOriginalRegistrySettings();
            }            
        }

        public bool IsAutoLogonConfigured()
        {
            //ToDo: Different user scenario
            
            //find out the path for startup process if it is same as current agent location, yes it was configured
            var regHelper = new AutoLogonRegistryManager(HostContext.GetService<IWindowsRegistryManager>(), null);
            var startupCommand = regHelper.GetStartupProcessCommand();

            if (string.IsNullOrEmpty(startupCommand))
            {
                return false;
            }

            var expectedStartupProcessDir = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin));
            return Path.GetDirectoryName(startupCommand).Equals(expectedStartupProcessDir, StringComparison.CurrentCultureIgnoreCase);
        }

        private void UpdateRegistriesForAutoLogon(AutoLogonRegistryManager regHelper, string userName, string domainName, string logonPassword)
        {
            regHelper.UpdateStandardRegistrySettings();

            //auto logon
            ConfigureAutoLogon(regHelper, userName, domainName, logonPassword);

            //startup process
            var startupProcessPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), "agent.listener.exe");
            var startupCommand = $@"{startupProcessPath} run";
            Trace.Info($"Setting startup command as {startupCommand}");

            regHelper.SetStartupProcessCommand(startupCommand);
        }

        private void ConfigureAutoLogon(AutoLogonRegistryManager regHelper, string userName, string domainName, string logonPassword)
        {
            //find out if the autologon was already enabled, show warning in that case
            ShowAutoLogonWarningIfAlreadyEnabled(regHelper, userName);
            _windowsServiceHelper.SetAutoLogonPassword(logonPassword);
            regHelper.UpdateAutoLogonSettings(userName, domainName);
        }

        private void ShowAutoLogonWarningIfAlreadyEnabled(AutoLogonRegistryManager regHelper, string userName)
        {
            regHelper.FetchAutoLogonUserDetails(out string autoLogonUserName, out string domainName);
            if (autoLogonUserName != null && !userName.Equals(autoLogonUserName, StringComparison.CurrentCultureIgnoreCase))
            {
                _terminal.WriteLine(StringUtil.Loc("AutoLogonAlreadyEnabledWarning", userName));
            }
        }

        private void DisplayWarningsIfAny(AutoLogonRegistryManager regHelper)
        {
            var warningReasons = regHelper.GetAutoLogonRelatedWarningsIfAny();
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

        private void ConfigurePowerOptions()
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

                        processInvoker.ExecuteAsync(
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