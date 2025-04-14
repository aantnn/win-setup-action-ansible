$installJson = "{{default_package_json_path}}"
$startupPath = "{{entry_point}}"
$MainCodeFile = "{{main_code}}"
$adminPassword = "{{admin_password}}"
$debugSerialPort = "COM"+([int]"{{debug_serial_port}}"+1) # numbering starts with 0 in QEMU

function Get-ConfigDrive {
    param (
        [Parameter(Mandatory=$true)]
        [string]$fileToFind
    )
    
    $drives = Get-PSDrive -PSProvider FileSystem
    foreach ($drive in $drives) {
        $driveLetter = $drive.Name + ":"
        $filePath = Join-Path -Path $driveLetter -ChildPath $fileToFind
        if (Test-Path $filePath) {
            return $driveLetter
        }
    }
    $errorMessage = "Configuration file '$fileToFind' not found on any drive. Please ensure the config file exists and is accessible."
    throw [System.IO.FileNotFoundException]::new($errorMessage, $fileToFind)
}


function Start-App() {
    if (-not (Test-Administrator)) {
        Start-ElevatedProcess
        return
    }
    Import-DotNetAssembly
    $adminUserName = Get-LocalizedAdminAccountName
    [WinImageBuilderAutomation]::EnableAdministratorAccount($adminUserName)
    [WinImageBuilderAutomation]::AddToAutoStart($startupPath)
    [WinImageBuilderAutomation]::Main2( $installJson, $driveLetter)
    if (-not (Test-RemoteManagementEnabled)) {
        Enable-RemoteManagement
    }
}

function Start-ElevatedProcess() {
    $osInfo = Get-WmiObject Win32_OperatingSystem
    $isServer2022 = $osInfo.Caption -like "*Server 2022*"
    $adminUserName = Get-LocalizedAdminAccountName
    if ($isServer2022) {
        Start-WinServer22-Elevated-With-RunAs($adminUserName)
        return
    }

    $PWord = ConvertTo-SecureString -String $adminPassword -AsPlainText -Force
    $adminCredential = New-Object -TypeName System.Management.Automation.PSCredential `
        -ArgumentList $adminUserName, $PWord
    Start-Process powershell.exe -Credential $adminCredential `
        -ArgumentList "-NoExit -ExecutionPolicy Bypass $PSCommandPath"
}

function Get-LocalizedAdminAccountName {
    try {
        # SID for the built-in Administrator account
        $adminSID = "S-1-5-21-%-500"

        # Get the Administrator account using the SID
        $adminAccount = Get-WmiObject Win32_UserAccount -Filter "SID like '$adminSID'"

        if ($adminAccount) {
            return $adminAccount.Name
        }
        else {
            Write-Warning "Unable to find the Administrator account."
            return $null
        }
    }
    catch {
        Write-Error "An error occurred while trying to get the Administrator account name: $_"
        return $null
    }
}


function Import-DotNetAssembly() {
    $sourceCode = [System.IO.File]::ReadAllText($MainCodeFile)
    $scriptAssembly = Get-NamesOfAssembliesToLoad @("System.Web.Extensions", 
        "System.Management")
    $osVersion = [System.Environment]::OSVersion
    if ($osVersion.Version.Major -eq 6 -and $osVersion.Version.Minor -eq 1) {
        $language = "CSharpVersion3"
    }
    else {
        $language = "CSharp"
    }
    Add-Type -ReferencedAssemblies $scriptAssembly -TypeDefinition $sourceCode -Language $language -IgnoreWarnings
}

function Get-NamesOfAssembliesToLoad {
    param(
        [string[]] $PartialNames
    )
    $fullNames = @()
    foreach ($name in $PartialNames) {
        $result = [System.Reflection.Assembly]::LoadWithPartialName($name)
        if ($result) {
            $fullNames += $result.FullName
        }
    }
    return $fullNames
}

function Test-Administrator {
    $currentUser = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Test-RemoteManagementEnabled {
    return (Get-Service WinRM).Status -eq "Running"
}

function Enable-RemoteManagement {
    [WinImageBuilderAutomation]::SetNetworksLocationToPrivate()
    Enable-PSRemoting -Force
    winrm quickconfig -q
    winrm set winrm/config/client/auth '@{Basic="true"}'
    winrm set winrm/config/service/auth '@{Basic="true"}'
    winrm set winrm/config/service '@{AllowUnencrypted="true"}'
    winrm set winrm/config/winrs '@{MaxMemoryPerShellMB="2048"}'
    Restart-Service -Name WinRM
    netsh advfirewall firewall add rule name="WinRM-HTTP" dir=in `
        localport=5985 protocol=TCP action=allow
}


function Start-WinServer22-Elevated-With-RunAs($adminUserName) {
    # DOES NOT WORK USE PWSH-CORE
    $csharpCode = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class RunAsCredentialManager
{
    static void Main()
    {
        RunAsCredentialManager.WriteCredential("Mainserver\\Administrator", "MainServer\\Ieuser", "Passw0rd!");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 3;

    public static bool WriteCredential(string targetName, string userName, string password)
    {
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        uint blobSize = (uint)passwordBytes.Length;

        CREDENTIAL cred = new CREDENTIAL
        {
            Flags = 8196,
            Type = CRED_TYPE_DOMAIN_PASSWORD,
            TargetName = targetName,
            CredentialBlobSize = blobSize,
            CredentialBlob = Marshal.AllocHGlobal((int)blobSize),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = userName
        };

        Marshal.Copy(passwordBytes, 0, cred.CredentialBlob, (int)blobSize);

        try
        {
            if (!CredWrite(ref cred, 0))
            {
                uint error = GetLastError();
                Console.Error.WriteLine("CredWrite failed with error code: {error}");
                return false;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }
}
"@

    # Add the C# type to the PowerShell session
    Add-Type -TypeDefinition $csharpCode -Language CSharp

    # To use with runas /cred
    [RunAsCredentialManager]::WriteCredential("$env:COMPUTERNAME\$adminUserName", "$env:COMPUTERNAME\$adminUserName", "$adminPassword")

    runas /savecred /user:"$env:COMPUTERNAME\$adminUserName" "powershell.exe -NoExit -ExecutionPolicy Bypass $PSCommandPath"

}

function Write-ToSerialPort {
    param (
        [Parameter(Mandatory=$true)]
        [string]$Message
    )
    if ($debugSerialPort) {
        $port = "COM$debugSerialPort"
        try {
            $serial = New-Object System.IO.Ports.SerialPort $port
            $serial.Open()
            $serial.Write($Message)
            $serial.Close()
        }
        catch {
            Write-Warning "Failed to write to serial port $port : $_"
        }
    }
}

try {
    $driveLetter = Get-ConfigDrive -FileToFind $installJson;
    $installJson = "$driveLetter\$installJson"
    $startupPath = "$driveLetter\$startupPath"
    $MainCodeFile = "$driveLetter\$MainCodeFile";
    Start-App
    exit
}
catch {
    $trace = $_.ScriptStackTrace
    $invocationInfo = $_.InvocationInfo
    # Log the error to serial port and file
    $errorMessage = $_.Exception.Message
    $fullErrorMessage = $_.Exception.ToString()
    $errorTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

    $errorLine = $invocationInfo.Line.Trim()

    $logEntry = @"
$errorTime - Error: $errorMessage
At:
   + $errorLine
Full Error: $fullErrorMessage 
Stack Trace:
$trace
"@
    try {
        $serialPort = New-Object System.IO.Ports.SerialPort($debugSerialPort, 115200)
        $serialPort.Open()
        # Write a special marker that Ansible can detect
        $serialPort.WriteLine("ANSIBLE_ERROR_MARKER_START")
        $serialPort.WriteLine($logEntry)
        $serialPort.WriteLine("ANSIBLE_ERROR_MARKER_END")
        $serialPort.Close()
    }
    catch {
        Write-Warning "Failed to write to serial port $debugSerialPort : $_"
    }

    Add-Content -Encoding utf8 -Path "$env:SYSTEMDRIVE\ansible-action-setup.log" -Value $logEntry

    # Display error in PowerShell
    Write-Error $logEntry
    throw

}