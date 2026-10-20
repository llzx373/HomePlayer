Root: HKLM; Subkey: "Software\Classes\HomePlayer.{#Ext}"; ValueType: string; ValueName: ""; ValueData: "{#Desc}"; Flags: uninsdeletekey; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\Classes\HomePlayer.{#Ext}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\Classes\HomePlayer.{#Ext}\shell\open"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\Classes\HomePlayer.{#Ext}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\Classes\.{#Ext}\OpenWithProgids"; ValueType: string; ValueName: "HomePlayer.{#Ext}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\HomePlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".{#Ext}"; ValueData: "HomePlayer.{#Ext}"; Flags: uninsdeletevalue; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".{#Ext}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc\{#Ext}
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.{#Ext}\shell\PlayWithHomePlayer"; ValueType: string; ValueName: ""; ValueData: "用 HomePlayer 播放"; Flags: uninsdeletekey; Tasks: assoc\{#Ext} context
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.{#Ext}\shell\PlayWithHomePlayer"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: assoc\{#Ext} context
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.{#Ext}\shell\PlayWithHomePlayer\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc\{#Ext} context
