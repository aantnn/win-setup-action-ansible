#!/usr/bin/python3
from __future__ import absolute_import, division, print_function
import os
import logging
from typing import Dict, Any, Optional, List, Union, Tuple
from dataclasses import dataclass

__metaclass__ = type

from ansible.plugins.action import ActionBase
from ansible.errors import (
    AnsibleError,
    AnsibleFileNotFound,
    AnsibleAction,
    AnsibleActionFail,
)

# Configure logging
logger = logging.getLogger(__name__)

# ===== File Constants =====
DEFAULT_ENTRY_POINT = "start.ps1"
DEFAULT_MAIN_CODE_FILE = "main.cs"
DEFAULT_INSTALL_JSON_PATH = "install.json"

# ===== Command Constants =====
DEFAULT_FIRST_LOGON_CMD = "start powershell.exe -NoExit -ExecutionPolicy Bypass -File"

# Search for start.ps1
DEFAULT_FIRST_LOGON_CMD = (
    "cmd.exe /C for %%D in (A B C D E F G H I J K L M N O P Q R S T U V W X Y Z) do @(if exist %%D:\\%s ( %s %%D:\\%s & goto :break) else (echo Not found)) & :break"
    % (DEFAULT_ENTRY_POINT, DEFAULT_FIRST_LOGON_CMD, DEFAULT_ENTRY_POINT)
)

# ===== Logging Constants =====
LOCK_FILE = "ansiblewinbuilder.lock"
DONE_LIST_FILE = "ansible-win-setup-done-list.log"

@dataclass
class StaticIPConfig:
    """Configuration for static IP settings."""
    interface_identifier: str
    ip_address: str
    routes_prefix: str
    next_hop_address: str
    dns_server_address: str
    secondary_dns_server: str

class WinSetupError(AnsibleError):
    """Custom exception for Windows setup errors."""
    pass

def validate_required_params(params: Dict[str, Any], required_params: List[str]) -> None:
    """
    Validate that all required parameters are present and not None.
    
    Args:
        params: Dictionary of parameters to validate
        required_params: List of required parameter names
        
    Raises:
        WinSetupError: If any required parameter is missing or None
    """
    missing_params = [param for param in required_params if param not in params or params[param] is None]
    if missing_params:
        raise WinSetupError(f"Missing required parameters: {', '.join(missing_params)}")

def image_index_xml_code(index: Optional[int]) -> str:
    """
    Generate XML code for image index.
    
    Args:
        index: Image index value
        
    Returns:
        str: Generated XML code
    """
    return f"""<InstallFrom>
            <MetaData wcm:action="add">
                <Key>/IMAGE/INDEX</Key>
                <Value>{index}</Value>
            </MetaData>
        </InstallFrom>"""

def static_ip_xml_code(task: Any, task_vars: Dict[str, Any]) -> str:
    """
    Generate XML code for static IP configuration.
    
    Args:
        task: Ansible task object
        task_vars: Task variables
        
    Returns:
        str: Generated XML code for static IP configuration
        
    Raises:
        WinSetupError: If static IP configuration is incomplete
    """
    static_ip_params = {
        "interface_identifier": "network_interface",
        "ip_address": "static_ip_address_cidr",
        "routes_prefix": "static_route_cidr",
        "next_hop_address": "static_gateway_ip",
        "dns_server_address": "static_dns_server",
        "secondary_dns_server": "static_secondary_dns_server"
    }

    # Extract parameters from task args
    config = StaticIPConfig(
        interface_identifier=task._task.args.get(static_ip_params["interface_identifier"]),
        ip_address=task._task.args.get(static_ip_params["ip_address"]),
        routes_prefix=task._task.args.get(static_ip_params["routes_prefix"]),
        next_hop_address=task._task.args.get(static_ip_params["next_hop_address"]),
        dns_server_address=task._task.args.get(static_ip_params["dns_server_address"]),
        secondary_dns_server=task._task.args.get(static_ip_params["secondary_dns_server"])
    )

    # Check if any parameter is provided
    if any(getattr(config, param) is not None for param in static_ip_params.keys()):
        # Validate all parameters are present
        missing_params = [param for param in static_ip_params.keys() 
                         if getattr(config, param) is None]
        if missing_params:
            raise WinSetupError(
                f"Incomplete static IP configuration. Missing parameters: {', '.join(missing_params)}"
            )

    # Generate XML for both architectures
    archs = ["x86", "amd64"]
    xml_components = []
    
    for arch in archs:
        xml_components.append(f"""<component name="Microsoft-Windows-TCPIP" processorArchitecture="{arch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <Interfaces>
                    <Interface wcm:action="add">
                        <Ipv4Settings>
                            <DhcpEnabled>false</DhcpEnabled>
                        </Ipv4Settings>
                        <Identifier>{config.interface_identifier}</Identifier>
                        <UnicastIpAddresses>
                            <IpAddress wcm:action="add" wcm:keyValue="1">{config.ip_address}</IpAddress>
                        </UnicastIpAddresses>
                        <Routes>
                            <Route wcm:action="add">
                                <Identifier>0</Identifier>
                                <Prefix>{config.routes_prefix}</Prefix>
                                <NextHopAddress>{config.next_hop_address}</NextHopAddress>
                            </Route>
                        </Routes>
                    </Interface>
                </Interfaces>
            </component>
            <component name="Microsoft-Windows-DNS-Client" processorArchitecture="{arch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <Interfaces>
                    <Interface wcm:action="add">
                        <Identifier>{config.interface_identifier}</Identifier>
                        <DNSServerSearchOrder>
                            <IpAddress wcm:action="add" wcm:keyValue="1">{config.dns_server_address}</IpAddress>
                            <IpAddress wcm:action="add" wcm:keyValue="2">{config.secondary_dns_server}</IpAddress>
                        </DNSServerSearchOrder>
                    </Interface>
                </Interfaces>
            </component>""")

    return "".join(xml_components)

class ActionModule(ActionBase):
    """Action module for Windows setup configuration."""
    
    _TEMPLATES_DIR = os.path.join(os.path.dirname(__file__), "templates")
    TRANSFERS_FILES = True
    _VALID_ARGS = frozenset((
        "config_output_dir",
        "image_index",
        "admin_password",
        "user_name",
        "user_password",
        "installation_steps",
        "computer_name",
        "first_logon_cmd",
        "network_interface",
        "static_ip_address_cidr",
        "static_route_cidr",
        "static_gateway_ip",
        "static_dns_server",
        "static_secondary_dns_server",
        "debug_serial_port"
    ))

    def _get_arg(self, key: str, default: Any = None) -> Any:
        """
        Get a task argument with a default value.
        
        Args:
            key: The argument key
            default: Default value if the argument is not present
            
        Returns:
            Any: The argument value or default
        """
        return self._task.args.get(key, default)

    def run(self, tmp=None, task_vars=None):
        """
        Run the action plugin.
        
        Args:
            tmp: Temporary directory
            task_vars: Task variables
            
        Returns:
            Dict[str, Any]: Action result
        """
        if task_vars is None:
            task_vars = dict()
            
        result = super(ActionModule, self).run(tmp, task_vars)
        del tmp  # tmp no longer has any effect
        
        try:
            # Validate required parameters
            validate_required_params(self._task.args, ["config_output_dir", "image_index", "installation_steps"])
            
            # Prepare task variables
            task_vars.update(self._prepare_template_vars(task_vars))
            
            # Process templates and files
            self._process_templates(task_vars)
            self._process_install_json(task_vars)
            
            result["changed"] = True
            result["msg"] = "Windows setup configuration completed successfully"
            
        except WinSetupError as e:
            logger.error(f"Windows setup error: {str(e)}")
            result["failed"] = True
            result["msg"] = str(e)
        except Exception as e:
            logger.error(f"Unexpected error during Windows setup: {str(e)}")
            result["failed"] = True
            result["msg"] = f"Unexpected error: {str(e)}"
        
        return result

    def _prepare_template_vars(self, task_vars: Dict[str, Any]) -> Dict[str, Any]:
        """
        Prepare variables for template processing.
        
        This method sets up all the variables needed for templating the various files:
        - autounattend.xml: Windows installation configuration
        - start.ps1: PowerShell entry point script
        - main.cs: C# automation code
        
        Args:
            task_vars: Task variables
            
        Returns:
            Dict[str, Any]: Prepared template variables
        """
        # ===== Windows Installation Configuration =====
        # Image index for Windows installation
        task_vars["from_image_xml_code"] = image_index_xml_code(
            self._get_arg("image_index")
        )
        
        # ===== File Paths =====
        # Base paths for all files
        dest_dir = self._get_arg("config_output_dir")
        
        # ===== User Account Settings =====
        # Administrator account
        task_vars["admin_password"] = self._get_arg("admin_password", "")
        task_vars["admin_user_name"] = self._get_arg("admin_user_name", "Administrator")
        
        # Regular user account
        task_vars["user_name"] = self._get_arg("user_name", "")
        task_vars["user_password"] = self._get_arg("user_password", "")
        
        # ===== System Configuration =====
        # Computer name settings
        task_vars["computer_name"] = self._get_arg("computer_name", "")
        
        # First logon command
        task_vars["first_logon_cmd"] = self._get_arg("first_logon_cmd", DEFAULT_FIRST_LOGON_CMD)
        
        # Debug serial port
        task_vars["debug_serial_port"] = self._get_arg("debug_serial_port", "")
        
        # ===== File Configuration =====
        # Paths and file names for the automation code
        task_vars["install_json"] = DEFAULT_INSTALL_JSON_PATH
        task_vars["entry_point"] = DEFAULT_ENTRY_POINT
        task_vars["main_code"] = DEFAULT_MAIN_CODE_FILE
        task_vars["default_package_json_path"] = DEFAULT_INSTALL_JSON_PATH
        task_vars["lock_file"] = LOCK_FILE
        task_vars["done_list_file"] = DONE_LIST_FILE
        
        # ===== Network Configuration =====
        # Static IP configuration if provided
        task_vars["static_ip_xml_code"] = static_ip_xml_code(self, task_vars)
        
        return task_vars

    def _process_templates(self, task_vars: Dict[str, Any]) -> None:
        """
        Process template files.
        
        Args:
            task_vars: Task variables
        """
        templates = ["autounattend.xml", "start.ps1", "main.cs"]
        dest = self._get_arg("config_output_dir")
        
        for template in templates:
            self._process_template(dest, template, task_vars)

    def _process_install_json(self, task_vars: Dict[str, Any]) -> None:
        """
        Process install.json file.
        
        Args:
            task_vars: Task variables
        """
        dest = os.path.join(self._get_arg("config_output_dir"), DEFAULT_INSTALL_JSON_PATH)
        self._copy_file(
            dest=dest,
            src=None,
            content=self._get_arg("installation_steps"),
            task_vars=task_vars
        )

    def _process_template(self, dest: str, filename: str, task_vars: Dict[str, Any]) -> Dict[str, Any]:
        """
        Process a template file.
        
        Args:
            dest: Destination directory
            filename: Template filename
            task_vars: Task variables
            
        Returns:
            Dict[str, Any]: Template processing result
        """
        template_args = {
            "src": os.path.join(self._TEMPLATES_DIR, filename),
            "dest": os.path.join(dest, filename),
            "newline_sequence": "\r\n"
        }

        template_task = self._task.copy()
        template_task.args = template_args

        template_action = self._shared_loader_obj.action_loader.get(
            "ansible.legacy.template",
            task=template_task,
            connection=self._connection,
            play_context=self._play_context,
            loader=self._loader,
            templar=self._templar,
            shared_loader_obj=self._shared_loader_obj,
        )
        return template_action.run(task_vars=task_vars)

    def _copy_file(self, src: Optional[str], dest: str, content: Optional[Any], task_vars: Dict[str, Any]) -> Dict[str, Any]:
        """
        Copy or create a file.
        
        Args:
            src: Source file path
            dest: Destination file path
            content: File content
            task_vars: Task variables
            
        Returns:
            Dict[str, Any]: Copy operation result
        """
        copy_args = {"dest": dest}
        if src is not None:
            copy_args["src"] = src
        else:
            copy_args["content"] = content

        copy_task = self._task.copy()
        copy_task.args = copy_args

        copy_action = self._shared_loader_obj.action_loader.get(
            "ansible.legacy.copy",
            task=copy_task,
            connection=self._connection,
            play_context=self._play_context,
            loader=self._loader,
            templar=self._templar,
            shared_loader_obj=self._shared_loader_obj,
        )
        return copy_action.run(task_vars=task_vars)
