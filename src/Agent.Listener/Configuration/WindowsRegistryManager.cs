#if OS_WINDOWS
using System;
using System.Collections.Generic;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(WindowsRegistryManager))]
    public interface IWindowsRegistryManager : IAgentService
    {
        string GetValue(RegistryHive hive, string subKeyName, string name);
        void SetValue(RegistryHive hive, string subKeyName, string name, string value);
        void DeleteValue(RegistryHive hive, string subKeyName, string name);
        bool RegsitryExists(string securityId);
    }

    public class WindowsRegistryManager : AgentService, IWindowsRegistryManager
    {
        public void DeleteValue(RegistryHive hive, string subKeyName, string name)
        {
            RegistryKey key = OpenRegistryKey(hive, subKeyName, true);
            using(key)
            {
                key.DeleteValue(name, false);
            }
        }

        public string GetValue(RegistryHive hive, string subKeyName, string name)
        {
            RegistryKey key = OpenRegistryKey(hive, subKeyName, false);
            using(key)
            {
                var value = key.GetValue(name, null);
                return value != null ? value.ToString() : null;
            }
        }

        public void SetValue(RegistryHive hive, string subKeyName, string name, string value)
        {
            RegistryKey key = OpenRegistryKey(hive, subKeyName, true);
            using(key)
            {
                key.SetValue(name, value);
            }
        }

        public bool RegsitryExists(string securityId)
        {
            return Registry.Users.OpenSubKey(securityId) != null;
        }

        private RegistryKey OpenRegistryKey(RegistryHive hive, string subKeyName, bool writable = true)
        {
            RegistryKey key = null;
            try
            {
                switch (hive)
                {
                    case RegistryHive.CurrentUser :
                        key = Registry.CurrentUser.OpenSubKey(subKeyName, writable);                    
                        break;
                    case RegistryHive.Users :
                        key = Registry.Users.OpenSubKey(subKeyName, writable);
                        break;
                    case RegistryHive.LocalMachine:
                        key = Registry.LocalMachine.OpenSubKey(subKeyName, writable);                    
                        break;
                }

                if (key == null)
                {
                    throw new InvalidOperationException(StringUtil.Loc("InvalidRegKey"));
                }

                return key;
            }
            catch(Exception ex)
            {
                Trace.Error(ex);
                throw;
            }
        }
    }
}
#endif