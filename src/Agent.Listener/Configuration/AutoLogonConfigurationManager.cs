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
        void UnConfigure();
        bool RestartNeeded();
        bool IsAutoLogonConfigured();
    }

    public class AutoLogonConfigurationManager : AgentService, IAutoLogonConfigurationManager
    {
        private ITerminal _terminal;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _terminal = hostContext.GetService<ITerminal>();
        }

        public void Configure(CommandSettings command)
        {
            AssertAdminAccess(false);

            var logonAccount = command.GetAutoLogonUserName();

            string domainName;
            string userName;

            GetAccountSegments(logonAccount, out domainName, out userName);

            if ((string.IsNullOrEmpty(domainName) || domainName.Equals(".", StringComparison.CurrentCultureIgnoreCase)) && !logonAccount.Contains("@"))
            {
                logonAccount = String.Format("{0}\\{1}", Environment.MachineName, userName);
            }

            Trace.Info("LogonAccount after transforming: {0}, user: {1}, domain: {2}", logonAccount, userName, domainName);

            string logonPassword = string.Empty;
            var windowsServiceHelper = HostContext.GetService<INativeWindowsServiceHelper>();
            while (true)
            {
                logonPassword = command.GetWindowsLogonPassword(logonAccount);
                if (windowsServiceHelper.IsValidCredential(domainName, userName, logonPassword))
                {
                    Trace.Info("Credential validation succeeded");
                    break;
                }
                
                if (command.Unattended)
                {
                    throw new SecurityException(StringUtil.Loc("InvalidWindowsCredential"));
                }
                    
                Trace.Info("Invalid credential entered");
                _terminal.WriteLine(StringUtil.Loc("InvalidWindowsCredential"));
            }

            bool isCurrentUserSameAsAutoLogonUser = windowsServiceHelper.HasActiveSession(domainName, userName);                
            var securityIdForTheUser = windowsServiceHelper.GetSecurityId(domainName, userName);
            var regManager = HostContext.GetService<IWindowsRegistryManager>();
            
            AutoLogonRegistryManager regHelper = isCurrentUserSameAsAutoLogonUser 
                                                ? new AutoLogonRegistryManager(regManager)
                                                : new AutoLogonRegistryManager(regManager, securityIdForTheUser);
            
            if(!isCurrentUserSameAsAutoLogonUser && !regHelper.ValidateIfRegistryExistsForTheUser(securityIdForTheUser))
            {
                Trace.Error(String.Format($"The autologon user '{logonAccount}' doesnt have a user profile on the machine. Please login once with the expected autologon user and reconfigure the agent again"));
                throw new InvalidOperationException("No user profile exists for the AutoLogon user");
            }

            DisplayWarningsIfAny(regHelper);        
            UpdateRegistriesForAutoLogon(regHelper, userName, domainName, logonPassword);
            ConfigurePowerOptions();
        }

        public bool RestartNeeded()
        {
            return !IsCurrentUserSameAsAutoLogonUser();
        }

        public void UnConfigure()
        {
            AssertAdminAccess(true);
            Trace.Info("Reverting the registry settings now.");
            AutoLogonRegistryManager regHelper = new AutoLogonRegistryManager(HostContext.GetService<IWindowsRegistryManager>());
            regHelper.RevertBackOriginalRegistrySettings();
        }

        public bool IsAutoLogonConfigured()
        {
            //ToDo: Different user scenario
            
            //find out the path for startup process if it is same as current agent location, yes it was configured
            var regHelper = new AutoLogonRegistryManager(HostContext.GetService<IWindowsRegistryManager>(), null);
            var startupCommand = regHelper.GetStartupProcessCommand();

            if(string.IsNullOrEmpty(startupCommand))
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
            var startupCommand = string.Format($@"{startupProcessPath} run");
            Trace.Verbose($"Setting startup command as {startupCommand}");

            regHelper.SetStartupProcessCommand(startupCommand);
        }

        private void ConfigureAutoLogon(AutoLogonRegistryManager regHelper, string userName, string domainName, string logonPassword)
        {
            //find out if the autologon was already enabled, show warning in that case
            ShowAutoLogonWarningIfAlreadyEnabled(regHelper, userName);

            var windowsHelper = HostContext.GetService<INativeWindowsServiceHelper>();
            windowsHelper.SetAutoLogonPassword(logonPassword);

            regHelper.UpdateAutoLogonSettings(userName, domainName);
        }

        private void ShowAutoLogonWarningIfAlreadyEnabled(AutoLogonRegistryManager regHelper, string userName)
        {
            regHelper.FetchAutoLogonUserDetails(out string autoLogonUserName, out string domainName);
            if(autoLogonUserName != null && !userName.Equals(autoLogonUserName, StringComparison.CurrentCultureIgnoreCase))
            {
                _terminal.WriteLine(string.Format(StringUtil.Loc("AutoLogonAlreadyEnabledWarning"), userName));
            }
        }

        private bool IsCurrentUserSameAsAutoLogonUser()
        {
            var regHelper = new AutoLogonRegistryManager(HostContext.GetService<IWindowsRegistryManager>());            
            regHelper.FetchAutoLogonUserDetails(out string userName, out string domainName);
            if(string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(domainName))
            {
                throw new InvalidOperationException("AutoLogon is not configured on the machine.");
            }

            var nativeWindowsHelper = HostContext.GetService<INativeWindowsServiceHelper>();
            return nativeWindowsHelper.HasActiveSession(domainName, userName);
        }

        private void DisplayWarningsIfAny(AutoLogonRegistryManager regHelper)
        {
            var warningReasons = regHelper.GetAutoLogonRelatedWarningsIfAny();
            if(warningReasons.Count > 0)
            {
                _terminal.WriteLine(StringUtil.Loc("UITestingWarning"));
                for(int i=0; i < warningReasons.Count; i++)
                {
                    _terminal.WriteLine(String.Format("{0} - {1}", (i+1).ToString(), warningReasons[i]));
                }
            }
        }

        private void ConfigurePowerOptions()
        {
            var whichUtil = HostContext.GetService<IWhichUtil>();
            var filePath = whichUtil.Which("powercfg.exe");
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
                            Trace.Verbose(message.Data);
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
                    _terminal.WriteLine(StringUtil.Loc("PowerOptionsConfigError"));
                    Trace.Error(ex);
                }
            }
        }

        private void AssertAdminAccess(bool unConfigure = false)
        {
            var windowsServiceHelper = HostContext.GetService<INativeWindowsServiceHelper>();
            if (windowsServiceHelper.IsRunningInElevatedMode())
            {
                return;
            }

            if(unConfigure)
            {
                Trace.Error("Needs Administrator privileges to unconfigure an agent running with AutoLogon capability.");          
                throw new SecurityException(StringUtil.Loc("NeedAdminForAutologonRemoval"));                
            }
            else
            {
                Trace.Error("Needs Administrator privileges to configure agent with AutoLogon capability.");
                Trace.Error("You will need to unconfigure the agent and then re-configure with Administrative rights");            
                throw new SecurityException(StringUtil.Loc("NeedAdminForAutologonCapability"));
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