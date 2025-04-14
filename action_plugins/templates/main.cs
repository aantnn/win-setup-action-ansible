using System;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Threading;




/*
* NOTE: Win7 SP1 installation forces reboot disregarding "/norestart" option
* https://social.technet.microsoft.com/Forums/ie/en-US/c4b7c3fc-037c-4e45-ab11-f6f64837521a/how-to-disable-reboot-after-sp1-installation-distribution-as-exe-via-sccm?forum=w7itproinstall
* It should continue installing after reboot skipping installed packages
*/
public class WinImageBuilderAutomation
{
    public static void Main()
    {
        // FOR TESTING ONLY
        string packageJsonPath = "\\install.json"; //templated by Ansible
        Main2(packageJsonPath, "E:");
        return;
    }
    public static void Main2(string packageJsonPath, string diskDrive)
    {
        Directory.SetCurrentDirectory(diskDrive);

        string doneList = Environment.GetEnvironmentVariable("SystemDrive")
            + "\\ansible-win-setup-done-list.log";

        List<ActionBase> actions = LoadAndDeserialize(packageJsonPath);

        actions.Sort(new ActionComparer()); // sort by Index property (priority)

        CheckDuplicateIndexes(actions);

        using (ActionTracker indexTracker = new ActionTracker(doneList))
        {
            foreach (IAction action in actions)
            {
                if (indexTracker.IsDone(action.Index))
                {
                    continue;
                }
                else
                {
                    action.Invoke();

                    indexTracker.Append(action.Index);
                    if (action.Restart)
                    {
                        indexTracker.Save();
                        Process.Start("shutdown", "/r /t 0");
                        Environment.Exit(0);
                        return;
                    }
                }
            }
            indexTracker.Save();
        }

        RemoveFromAutoStart();
    }



    private static List<ActionBase> LoadAndDeserialize(string packageJsonPath)
    {
        List<ActionBase> actions = new List<ActionBase>();
        string packageJsonContent = File.ReadAllText(packageJsonPath);
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        List<JavaScriptConverter> converters = new List<JavaScriptConverter> { new CustomDispatchConverter() };
        serializer.RegisterConverters(converters);
        actions = serializer.Deserialize<List<ActionBase>>(packageJsonContent);
        return actions;
    }

    public static void AddToAutoStart(string startupPath)
    {
        Dictionary<string, object> mainPs1Autostart = new Dictionary<string, object>();
        mainPs1Autostart["state"] = "present";
        mainPs1Autostart["keyname"] = "start.ps1";
        mainPs1Autostart["interpreter"] = "powershell.exe -NoExit -ExecutionPolicy Bypass -File";
        mainPs1Autostart["target"] = startupPath;
        mainPs1Autostart["args"] = "";

        AutostartAction autostartAction = new AutostartAction(mainPs1Autostart);
        autostartAction.Invoke();
    }
    private static void RemoveFromAutoStart()
    {
        Dictionary<string, object> mainPs1Autostart = new Dictionary<string, object>();
        mainPs1Autostart["state"] = "absent";
        mainPs1Autostart["keyname"] = "start.ps1";

        AutostartAction autostartAction = new AutostartAction(mainPs1Autostart);
        autostartAction.Invoke();
    }
    private static void CheckDuplicateIndexes(List<ActionBase> actions)
    {
        HashSet<int> indexes = new HashSet<int>();

        foreach (IAction action in actions)
        {
            if (indexes.Contains(action.Index))
            {
                throw new InvalidOperationException("Duplicate index found, action id: " + action.Index
                    + " Action data: " + action.ToString());
            }

            indexes.Add(action.Index);
        }
    }

    public static void SetNetworksLocationToPrivate()
    {
        INetworkListManager nlm = (INetworkListManager)new NetworkListManagerClass();
        IEnumerable networks = nlm.GetNetworks(NetworkConnectivityLevels.All);
        foreach (INetwork network in networks)
        {
            network.SetCategory(NetworkCategory.Private);
        }
    }
    public static void EnableAdministratorAccount(string accountName)
    {
        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_UserAccount WHERE Name='" + accountName + "'"))
        {
            ManagementObjectCollection accounts = (ManagementObjectCollection)searcher.Get();
            foreach (ManagementObject account in accounts)
            {
                if (account != null)
                {
                    if ((bool)account["Disabled"])
                    {
                        account["Disabled"] = false;
                        account.Put();
                    }
                }
            }
        }
    }
}



public class ActionTracker : IDisposable
{
    private StreamWriter writer;
    private IDictionary<int, string> indexTracker;
    public ActionTracker(string path)
    {
        FileStream f = File.Open(path, FileMode.Append);
        f.Close();

        indexTracker = new Dictionary<int, string>();
        using (StreamReader reader = new StreamReader(path))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length > 0)
                    indexTracker.Add(int.Parse(line), "");
            }
        }
        writer = File.AppendText(path);
    }
    public bool IsDone(int index)
    {
        return indexTracker.ContainsKey(index);
    }

    public void Append(int index)
    {
        indexTracker.Add(index, "");
        writer.WriteLine(index);
    }
    public void Save()
    {
        writer.Flush();
    }
    public void Dispose()
    {
        Save();
        writer.Dispose();
    }
}




internal class ActionComparer : IComparer<ActionBase>
{
    public int Compare(ActionBase a, ActionBase b)
    {
        int indexA = a.Index;
        int indexB = b.Index;
        return indexA.CompareTo(indexB);
    }
}



internal class CustomDispatchConverter : JavaScriptConverter
{
    public override object Deserialize(IDictionary<string, object> dictionary, Type type, JavaScriptSerializer serializer)
    {
        object indexValue;
        if (!dictionary.TryGetValue("index", out indexValue))
        {
            throw new ArgumentException("The 'index' field is missing. Action data: " + dictionary.ToString());
        }
        if (!(indexValue is int))
        {
            throw new ArgumentException("The 'index' field is invalid: " + indexValue.ToString()
                + " Action data: " + dictionary.ToString());
        }
        IAction action = CreateActionFromDictionary(dictionary);
        action.Index = (int)indexValue;

        action.Restart = false;
        object restartValue;
        if (dictionary.TryGetValue("restart", out restartValue))
        {
            action.Restart = (bool)restartValue;
        }

        return action;
    }
    private IAction CreateActionFromDictionary(IDictionary<string, object> dictionary)
    {
        object actionData;

        if (dictionary.TryGetValue("file", out actionData))
        {
            return new FileAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("zip", out actionData))
        {
            return new UnzipAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("msi", out actionData))
        {
            return new MsiAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("exe", out actionData))
        {
            return new ExeAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("msu", out actionData))
        {
            return new MsuAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("cab", out actionData))
        {
            return new DismAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("copy", out actionData))
        {
            return new CopyAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("cmd", out actionData))
        {
            return new CmdAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("registry", out actionData))
        {
            return new RegistryAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("path", out actionData))
        {
            return new PathAction((Dictionary<string, object>)actionData);
        }
        if (dictionary.TryGetValue("autostart", out actionData))
        {
            return new AutostartAction((Dictionary<string, object>)actionData);
        }

        throw new Exception("Unknown action type. Action data: " + dictionary.ToString());
    }


    public override IDictionary<string, object> Serialize(object obj, JavaScriptSerializer serializer)
    {
        throw new NotImplementedException();
    }
    public override IEnumerable<Type> SupportedTypes
    {
        get { return new List<Type>(new List<Type>(new[] { typeof(object) })); }
    }

}




internal interface IAction
{
    int Index { get; set; }
    bool Restart { get; set; }

    void Invoke();
}
internal abstract class ActionBase : IAction
{
    public ActionBase() { }
    public int Index { get; set; }
    public bool Restart { get; set; }


    public abstract void Invoke();

    // Modified to be more compatible with .NET 2.0
    protected T TryGetValue<T>(IDictionary<string, object> item, string key, T defaultValue)
    {
        object value;
        if (item.TryGetValue(key, out value))
        {
            if (value is T)
            {
                return (T)value;
            }
            else
            {
                throw new ArgumentException("Value is not of type " + typeof(T).Name);
            }
        }
        if (defaultValue == null)
        {
            throw new ArgumentException("Missing or invalid keys");
        }
        return defaultValue;
    }
}


internal class FileAction : ActionBase
{
    private string path;
    public enum State
    {
        Directory,
        Touch,
        Absent
    }
    private State state;


    public FileAction(IDictionary<string, object> actionData)
    {
        try
        {
            path = (string)actionData["path"];
            state = (State)Enum.Parse(typeof(State), (string)actionData["state"], true);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("FileAction: Invalid argument or missing key. Action data: "
                + actionData.ToString(), ex);
        }
    }
    public override void Invoke()
    {
        //mimic ansible (does 'file' state do anything?)
        switch (state)
        {
            case State.Directory:
                Directory.CreateDirectory(path);
                break;

            case State.Touch:
                DateTime newLastWriteTime = DateTime.Now;
                if (!Directory.Exists(path))
                {

                    if (!File.Exists(path))
                    {
                        File.Create(path);
                    }
                    else
                    {
                        File.SetLastWriteTime(path, newLastWriteTime);
                    }
                }
                else
                {
                    Directory.SetLastWriteTime(path, newLastWriteTime);
                }
                break;


            case State.Absent:
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                else
                {
                    throw new IOException("FileAction: Path does not exist. Action data: " + this.ToString());
                }
                break;
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

internal class RegistryAction : ActionBase
{
    public enum RegistryState
    {
        Present,
        Absent
    }
    private string path { get; set; }
    private string name { get; set; }
    private object data { get; set; }
    private RegistryValueKind itemType { get; set; }
    private RegistryState state { get; set; }

    public RegistryAction(Dictionary<string, object> actionData)
    {
        try
        {
            path = TryGetValue<string>(actionData, "name", null);
        }
        catch
        {
            throw new ArgumentException("RegistryAction: path is required. Action data: " + actionData.ToString());
        }
        name = TryGetValue(actionData, "name", "Default");

        data = null;
        object dataValue;
        if (actionData.TryGetValue("data", out dataValue))
        {
            data = dataValue;
        }

        itemType = ParseRegistryItemType(TryGetValue(actionData, "type", "String"));
        state = ParseRegistryState((string)actionData["state"]);
    }
    private static RegistryState ParseRegistryState(string state)
    {
        RegistryState parsedState;
        try
        {
            parsedState = (RegistryState)Enum.Parse(typeof(RegistryState), state, true);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("RegistryAction: Invalid registry state: " + state);
        }
        return parsedState;
    }

    private static RegistryValueKind ParseRegistryItemType(string type)
    {
        RegistryValueKind parsedType;
        try
        {
            parsedType = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), type, true);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("RegistryAction: Invalid registry item type: " + type);
        }
        return parsedType;
    }

    public override void Invoke()
    {
        switch (state)
        {
            case RegistryState.Present:
                CreateOrUpdateRegistryKey();
                break;
            case RegistryState.Absent:
                DeleteRegistryKeyOrValue();
                break;
            default:
                throw new InvalidOperationException("RegistryAction: Unsupported registry state: "
                    + state + " Action data: " + this.ToString());
        }
    }
    private RegistryKey OpenBaseKey(string path)
    {
        int hiveEndIndex = path.IndexOf(':');
        if (hiveEndIndex < 0)
        {
            throw new ArgumentException("RegistryAction: Invalid registry key path Action data: " + this.ToString());
        }

        string hive = path.Substring(0, hiveEndIndex);
        string keyPath = path.Substring(hiveEndIndex + 1);

        RegistryKey baseKey;

        switch (hive.ToUpper())
        {
            case "HKCC":
                baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.CurrentConfig, null);
                break;
            case "HKCR":
                baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.ClassesRoot, null);
                break;
            case "HKCU":
                baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.CurrentUser, null);
                break;
            case "HKLM":
                baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, null);
                break;
            case "HKU":
                baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.Users, null);
                break;
            default:
                throw new ArgumentException("RegistryAction: Invalid registry hive: "
                    + hive + " Action data: " + this.ToString());
        }

        return baseKey;
    }

    private void CreateOrUpdateRegistryKey()
    {
        using (RegistryKey baseKey = OpenBaseKey(path))
        {
            using (RegistryKey key = baseKey.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("RegistryAction: Failed to create or open registry key: " + path
                        + " Action data: " + this.ToString());
                }
                key.SetValue(name, data);
            }
        }
    }


    private void DeleteRegistryKeyOrValue()
    {

        using (RegistryKey baseKey = OpenBaseKey(path))
        {
            using (RegistryKey key = baseKey.OpenSubKey(path, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("RegistryAction: Failed to open registry key: " + path
                        + " Action data: " + this.ToString());
                }

                if (name == "Default")
                {
                    key.DeleteSubKeyTree(path);
                }
                else
                {
                    key.DeleteValue(name);
                }
            }
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

//shell32 may fail but it tries to insert workdir and then search second time
internal class UnzipAction : ActionBase
{
    private string zipPath;
    private string extractPath;
    private string current;

    public UnzipAction(IDictionary<string, object> actionData)
    {
        try
        {   // setcurrentdir does not work for shell32
            current = Directory.GetCurrentDirectory().Substring(0, 2);
            zipPath = (string)actionData["path"];
            extractPath = (string)actionData["dest"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("ZipAction: Invalid argument or missing key"
                + " Action data: " + actionData.ToString(), ex);
        }
    }


    public override void Invoke()
    {
        Type type = Type.GetTypeFromProgID("Shell.Application");
        Shell32.IShellDispatch shell = (Shell32.IShellDispatch)Activator.CreateInstance(type);

        Shell32.Folder source = shell.NameSpace(zipPath);
        Shell32.Folder destination = shell.NameSpace(extractPath);

        if (source == null)
        {
            if ((source = shell.NameSpace(current + zipPath)) == null)
                throw new FileNotFoundException("Zip file not found: " + zipPath
                    + " Action data: " + this.ToString());
        }
        if (destination == null)
        {
            if ((destination = shell.NameSpace(current + extractPath)) == null)
                throw new DirectoryNotFoundException("Destination directory not found: " + extractPath
                    + " Action data: " + this.ToString());
        }

        foreach (object item in source.Items())
        {
            destination.CopyHere(item, 8 | 16 | 512 | 1024);
        }

    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

internal class ExeAction : ActionBase
{
    protected string packagePath;
    protected string arguments;

    public ExeAction(IDictionary<string, object> actionData)
    {
        try
        {
            packagePath = (string)actionData["path"];
            arguments = (string)actionData["args"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("ExeAction: Invalid argument or missing key. Action data: "
                + actionData.ToString(), ex);
        }
    }


    public override void Invoke()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = packagePath;
        startInfo.Arguments = arguments;
        startInfo.WindowStyle = ProcessWindowStyle.Normal;
        Process.Start(startInfo).WaitForExit();
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

internal class MsuAction : ActionBase
{
    private const string wusa = "wusa.exe";
    private string arguments;
    private string package;
    public MsuAction(IDictionary<string, object> actionData)
    {
        try
        {
            package = (string)actionData["path"];
            arguments = package + " " + (string)actionData["args"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("MsuAction: Invalid argument or missing key. Action data: "
                + actionData.ToString(), ex);
        }
    }

    public override void Invoke()
    // TODO think about wusa errors, because it may detach
    {
        if (!File.Exists(package))
        {
            throw new ArgumentException("Msu file does not exists: " + package
                + " Action data: " + this.ToString());
        }
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = wusa;
        startInfo.Arguments = arguments;
        startInfo.UseShellExecute = false;
        startInfo.WindowStyle = ProcessWindowStyle.Normal;
        Process.Start(startInfo).WaitForExit();
        //additional waiting on wusa.exe
        WaitProcess(wusa);
    }
    private void WaitProcess(string name)
    {
        int instanceCount;
        do
        {
            System.Threading.Thread.Sleep(2000);
            instanceCount = Process.GetProcessesByName(name).Length;
        }
        while (instanceCount > 0);
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

internal class MsiAction : ActionBase
{
    [DllImport("msi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern UInt32 MsiInstallProduct([MarshalAs(UnmanagedType.LPTStr)] string packagePath,
        [MarshalAs(UnmanagedType.LPTStr)] string commandLine);

    private string packagePath;
    private string arguments;

    public MsiAction(IDictionary<string, object> actionData)
    {
        try
        {
            packagePath = (string)actionData["path"];
            arguments = (string)actionData["args"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("MsiAction: Invalid argument or missing key. "
                + " Action data: " + actionData.ToString(), ex);
        }
    }

    public override void Invoke()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder(arguments);
        //args format Property=Setting Property=Setting.
        uint result = MsiInstallProduct(packagePath, sb.ToString());
        if (result != 0)
        {
            throw new Exception("MsiInstallProduct failed. Error: " + result
                 + " Action data: " + this.ToString());
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}
class DismAction : ActionBase
{
    private const string DismAssembly = "DismApi.dll";
    private const string DISM_ONLINE_IMAGE = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}"; // Placeholder value, you need to use the actual constant from the DISM API
    private string packagePath;
    private bool ignoreCheck;
    private bool preventPending;


    [DllImport(DismAssembly, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Error)]
    public static extern int DismOpenSession(string imagePath, string windowsDirectory, string systemDrive, out IntPtr session);

    [DllImport(DismAssembly, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Error)]
    public static extern int DismAddPackage(IntPtr session, string packagePath, bool ignoreCheck, bool preventPending, IntPtr progress, IntPtr userData, IntPtr callback);

    [DllImport(DismAssembly, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Error)]
    public static extern int DismCloseSession(IntPtr session);

    public DismAction(IDictionary<string, object> actionData)
    {
        try
        {
            packagePath = TryGetValue<string>(actionData, "path", null);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("DismAction: The action dictionary must contain a 'path' key. "
            + " Action data: " + actionData.ToString(), ex);
        }

        ignoreCheck = TryGetValue(actionData, "ignorecheck", false);
        preventPending = TryGetValue(actionData, "preventpending", false);

    }

    public override void Invoke()
    {
        IntPtr session;
        int result = DismOpenSession(DISM_ONLINE_IMAGE, null, null, out session);
        if (result != 0)
        {
            throw new Exception("DismAction: Failed to open DISM session for the online image. "
                + " Action data: " + this.ToString());
        }

        try
        {
            result = DismAddPackage(session, packagePath, ignoreCheck, preventPending, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result != 0)
            {
                throw new Exception("DismAction: Failed to add package to the online image. "
                     + " Action data: " + this.ToString());
            }
        }
        finally
        {
            result = DismCloseSession(session);
            if (result != 0)
            {
                throw new Exception("DismAction: Failed to close DISM session for the online image. "
                    + " Action data: " + this.ToString());
            }
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

internal class CopyAction : ActionBase
{
    private string source;
    private string destination;
    private bool force;
    private string content;

    public CopyAction(IDictionary<string, object> actionData)
    {
        try
        {
            object _src;
            if (actionData.TryGetValue("content", out _src))
            {
                source = (string)_src;
            }
            else
            {
                content = (string)actionData["content"];
            }
            destination = (string)actionData["dest"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("CopyAction: Invalid argument or missing key"
                + " Action data: " + actionData.ToString(), ex);
        }

        force = TryGetValue(actionData, "force", false);
    }

    public override void Invoke()
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return; // do nothing
        }
        try
        {
            if (Directory.Exists(source))
            {
                DirectoryInfo diSource = new DirectoryInfo(source);
                DirectoryInfo diTarget = new DirectoryInfo(destination);
                CopyAll(diSource, diTarget);
            }
            else if (File.Exists(source))
            {
                if (content.Equals(String.Empty))
                {

                    File.Copy(source, destination, force);
                }
                else
                {
                    File.WriteAllText(content, destination);
                }
            }
            else
            {
                throw new ArgumentException("Source path does not exists: " + source
                    + " Action data: " + this.ToString());
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("CopyAction: Exception occurred "
                + " Action data: " + this.ToString(), ex);
        }
    }
    private static void CopyAll(DirectoryInfo source, DirectoryInfo target)
    {
        if (source.FullName.ToLower() == target.FullName.ToLower())
        {
            return;
        }
        if (Directory.Exists(target.FullName) == false)
        {
            Directory.CreateDirectory(target.FullName);
        }

        foreach (FileInfo fi in source.GetFiles())
        {
            fi.CopyTo(Path.Combine(target.ToString(), fi.Name), true);
        }

        foreach (DirectoryInfo diSourceSubDir in source.GetDirectories())
        {
            DirectoryInfo nextTargetSubDir =
                target.CreateSubdirectory(diSourceSubDir.Name);
            CopyAll(diSourceSubDir, nextTargetSubDir);
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}

internal class CmdAction : ActionBase
{
    private string command;

    public CmdAction(IDictionary<string, object> actionData)
    {
        try
        {
            command = (string)actionData["cmd"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("CmdAction: Invalid argument or missing key, hint use cmd:\ncmd: 'command'"
                + " Action data: " + actionData.ToString(), ex);
        }
    }


    public override void Invoke()
    {
        Console.WriteLine("Running command: " + command);
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/C " + command + " & exit /b %errorlevel% ";
            startInfo.WindowStyle = ProcessWindowStyle.Normal;
            Process process = Process.Start(startInfo);
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            // Handle any exceptions that may occur during the command execution
            Console.WriteLine("CmdAction: An error occurred while running the command: " + ex.Message
                + " Action data: " + this.ToString());
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}



internal class PathAction : ActionBase
{
    private string pathToModify;
    private PathState state;

    // Define an enum for the state
    public enum PathState
    {
        Present,
        Absent
    }

    public PathAction(IDictionary<string, object> actionData)
    {
        string stateStr;
        try
        {
            pathToModify = (string)actionData["path"];
            stateStr = (string)actionData["state"];
        }
        catch (Exception ex)
        {
            throw new ArgumentException("PathAction: Invalid argument or missing key"
                + " Action data: " + actionData.ToString(), ex);
        }

        try
        {
            state = (PathState)Enum.Parse(typeof(PathState), stateStr, true);
        }
        catch (ArgumentException)
        {
            // Handle the case where the string does not represent a valid state
            throw new ArgumentException("PathAction: Invalid state value. Only 'Present' or 'Absent' are supported"
                + " Action data: " + actionData.ToString());
        }

    }

    public override void Invoke()
    {
        string currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
        string[] paths = currentPath.Split(new char[] { ';' });
        List<string> pathList = new List<string>();

        foreach (string path in paths)
        {
            if (path != null && path.Length > 0)
            {
                pathList.Add(path);
            }
        }

        switch (state)
        {
            case PathState.Present:
                if (!pathList.Contains(pathToModify))
                {
                    pathList.Add(pathToModify);
                    string newPath = string.Join(";", pathList.ToArray());
                    Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Machine);
                }
                break;
            case PathState.Absent:
                if (pathList.Contains(pathToModify))
                {
                    pathList.Remove(pathToModify);
                    string newPath = string.Join(";", pathList.ToArray());
                    Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Machine);
                }
                break;
            default:
                throw new ArgumentException("PathAction: Invalid state value. Only 'Present' or 'Absent' are supported."
                    + " Action data: " + this.ToString());
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }
}



internal class AutostartAction : ActionBase
{
    private string keyName;
    private string interpreter;
    private string args;
    private string target;
    private State state;
    public enum State
    {
        Present,
        Absent
    }

    public AutostartAction(IDictionary<string, object> actionData)
    {
        try
        {
            keyName = TryGetValue<string>(actionData, "keyname", null);
            state = (State)Enum.Parse(typeof(State), TryGetValue<string>(actionData, "state", null), true);
            interpreter = TryGetValue(actionData, "interpreter", "");
            target = TryGetValue(actionData, "target", "");
            args = TryGetValue(actionData, "args", "");
        }
        catch
        {
            throw new ArgumentException("AutostartAction: Missing or invalid key."
                + " Action data: " + actionData.ToString());
        }
    }


    public override void Invoke()
    {
        string value = string.Format("cmd /C \"{0} {1} {2} \"", interpreter, target, args);

        using (RegistryKey key = Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
        {
            if (key != null)
            {
                switch (state)
                {
                    case State.Present:
                        key.SetValue(keyName, value, RegistryValueKind.String);
                        break;
                    case State.Absent:
                        key.DeleteValue(keyName);
                        break;
                }
            }
            else
            {
                throw new Exception("Unable to open registry key for modification."
                    + " Action data: " + this.ToString());
            }
        }
    }
    public override string ToString()
    {
        FieldInfo[] properties = this.GetType().GetFields(BindingFlags.NonPublic |
            BindingFlags.Instance);
        string result = "";

        foreach (FieldInfo property in properties)
        {
            result += property.Name + ": " + property.GetValue(this) + " \n";
        }

        return result;
    }

}


public enum NetworkCategory { Public, Private, Authenticated }
[Flags]
public enum NetworkConnectivityLevels { Connected = 1, Disconnected = 2, All = 3 }
[ComImport]
[Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
[TypeLibType((short)0x1040)]
internal interface INetworkListManager
{
    [return: MarshalAs(UnmanagedType.Interface)]
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    IEnumerable GetNetworks([In] NetworkConnectivityLevels Flags);
}
[ComImport, ClassInterface((short)0), Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
internal class NetworkListManagerClass { }
[ComImport]
[TypeLibType((short)0x1040)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
[Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
internal interface INetwork
{
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    void SetCategory([In] NetworkCategory NewCategory);
}

namespace Shell32
{
    [Guid("FAC32C80-CBE4-11CE-8350-444553540000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface FolderItem { }

    [ComImport]
    [Guid("744129E0-CBE5-11CE-8350-444553540000")]
    [TypeLibType(4160)]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface FolderItems : IEnumerable
    {
        [DispId(1610743808)]
        int Count
        {
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            [DispId(1610743808)]
            get;
        }

        [DispId(1610743809)]
        object Application
        {
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            [DispId(1610743809)]
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }

        [DispId(1610743810)]
        object Parent
        {
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            [DispId(1610743810)]
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        [DispId(1610743811)]
        [return: MarshalAs(UnmanagedType.Interface)]
        FolderItem Item([Optional][In][MarshalAs(UnmanagedType.Struct)] object index);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        [DispId(-4)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "System.Runtime.InteropServices.CustomMarshalers.EnumeratorToEnumVariantMarshaler, CustomMarshalers")]
        new IEnumerator GetEnumerator();
    }


    [ComImport, Guid("BBCBDE60-C3FF-11CE-8350-444553540000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface Folder
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime), DispId(0x60020004)]
        FolderItems Items();
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime), DispId(0x60020008)]
        void CopyHere([In, MarshalAs(UnmanagedType.Struct)] object vItem, [In, Optional, MarshalAs(UnmanagedType.Struct)] object vOptions);
    }


    [ComImport, Guid("D8F015C0-C278-11CE-A49E-444553540000"), TypeLibType((short)0x1050)]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IShellDispatch
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime), DispId(0x60020002)]
        Folder NameSpace([In, MarshalAs(UnmanagedType.Struct)] object vDir);
    }
}

