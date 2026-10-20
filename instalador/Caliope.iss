; Instalador de Calíope (Inno Setup 6).
; Compilar con instalador\crear-instalador.ps1, que publica la aplicación y llama a ISCC.
; Incluye la aplicación con .NET; los motores y modelos de voz se descargan desde la propia aplicación.

#ifndef Version
  #define Version "1.0.0"
#endif
#define Nombre "Calíope"
#define Exe "Caliope.exe"
#define Repositorio "https://github.com/rboluda/Caliope"

[Setup]
AppId={{6F0B8E3C-2A1D-4C7B-9E55-C411A0F3D7E2}
AppName={#Nombre}
AppVersion={#Version}
AppVerName={#Nombre} {#Version}
AppPublisher=rboluda
AppPublisherURL={#Repositorio}
AppSupportURL={#Repositorio}/issues
AppUpdatesURL={#Repositorio}/releases
VersionInfoVersion={#Version}
VersionInfoProductName={#Nombre}
VersionInfoDescription=Instalador de {#Nombre}
; Por usuario y sin permisos de administrador; quien quiera puede instalarlo para todos.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Caliope
DefaultGroupName={#Nombre}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\recursos\caliope.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#Nombre}
WizardStyle=modern
WizardImageFile=lateral-100.bmp,lateral-150.bmp,lateral-200.bmp
WizardSmallImageFile=pequena-55.bmp,pequena-64.bmp,pequena-83.bmp,pequena-92.bmp,pequena-110.bmp,pequena-119.bmp,pequena-138.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
OutputDir=..\publish
OutputBaseFilename=Caliope-{#Version}-Instalador

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.Escritorio=Crear un acceso directo en el escritorio
en.Escritorio=Create a desktop shortcut
es.Abrir=Abrir {#Nombre}
en.Abrir=Launch {#Nombre}
es.Bienvenida=Calíope convierte textos en voz en tu propio ordenador: diseña voces, clónalas y narra guiones completos sin conexión.%n%nEste instalador solo copia la aplicación (unos 150 MB). Al abrirla por primera vez, un asistente te propondrá qué motores y modelos de voz descargar según tu equipo (entre 5 y 20 GB).
en.Bienvenida=Calíope turns text into speech on your own computer.%n%nThis installer only copies the application. On first launch, a wizard will suggest which voice engines and models to download for your hardware.
es.Datos=Tus voces, proyectos y modelos descargados no se borran al desinstalar: siguen en la carpeta de datos que elegiste en el asistente.
en.Datos=Your voices, projects and downloaded models are kept after uninstalling, in the data folder you chose.

[Messages]
es.WelcomeLabel2=[name/ver]%n%n{cm:Bienvenida}
en.WelcomeLabel2=[name/ver]%n%n{cm:Bienvenida}

[Tasks]
Name: "escritorio"; Description: "{cm:Escritorio}"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#Nombre}"; Filename: "{app}\{#Exe}"
Name: "{autodesktop}\{#Nombre}"; Filename: "{app}\{#Exe}"; Tasks: escritorio

[Run]
Filename: "{app}\{#Exe}"; Description: "{cm:Abrir}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Restos que la aplicación crea junto al ejecutable (caché de Python del motor).
Type: filesandordirs; Name: "{app}\motores"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox(ExpandConstant('{cm:Datos}'), mbInformation, MB_OK);
end;
