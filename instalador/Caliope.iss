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
ShowLanguageDialog=auto

[Languages]
; El primero (castellano) es el predeterminado; Inno elige solo el del sistema y únicamente pregunta si no coincide ninguno.
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.Escritorio=Crear un acceso directo en el escritorio
en.Escritorio=Create a desktop shortcut
es.Abrir=Abrir {#Nombre}
en.Abrir=Launch {#Nombre}
es.Bienvenida=Calíope convierte textos en voz en tu propio ordenador: diseña voces, clónalas y narra guiones completos sin conexión.%n%nEste instalador solo copia la aplicación (unos 150 MB). Al abrirla por primera vez, un asistente te propondrá qué motores y modelos de voz descargar según tu equipo (entre 5 y 20 GB).
en.Bienvenida=Calíope turns text into speech on your own computer: design voices, clone them and narrate full scripts offline.%n%nThis installer only copies the application (about 150 MB). The first time you open it, a wizard will suggest which voice engines and models to download for your computer (between 5 and 20 GB).
es.Datos=Tus voces, narraciones y modelos se han conservado en:%n%1
en.Datos=Your voices, narrations and models have been kept in:%n%1
es.Borrado=Listo. Se ha liberado el espacio de los modelos y motores en:%n%1
en.Borrado=Done. The space used by models and engines has been freed in:%n%1
es.BorradoTodo=Listo. Se han borrado todos los datos de Calíope de:%n%1
en.BorradoTodo=Done. All Calíope data has been deleted from:%n%1
es.Pregunta=¿Qué quieres hacer con los datos de Calíope?
en.Pregunta=What do you want to do with Calíope's data?
es.Carpeta=Están en: %1
en.Carpeta=They are in: %1
es.Conservar=Conservarlo todo (recomendado si vas a volver a instalar Calíope)
en.Conservar=Keep everything (recommended if you will reinstall Calíope)
es.BorrarModelos=Borrar los modelos y motores para liberar espacio (conserva mis voces y narraciones)
en.BorrarModelos=Delete models and engines to free space (keep my voices and narrations)
es.BorrarTodo=Borrarlo todo, también mis voces y narraciones
en.BorrarTodo=Delete everything, including my voices and narrations
es.Aviso=Los modelos ocupan varios GB; si vuelves a instalar Calíope tendrá que descargarlos otra vez.
en.Aviso=The models take up several GB; if you install Calíope again, it will have to download them again.
es.Continuar=Desinstalar
en.Continuar=Uninstall
es.Cancelar=Cancelar
en.Cancelar=Cancel

[Messages]
es.WelcomeLabel2=[name/ver]%n%n{cm:Bienvenida}
en.WelcomeLabel2=[name/ver]%n%n{cm:Bienvenida}

[Tasks]
Name: "escritorio"; Description: "{cm:Escritorio}"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Licencias de lo que se incluye y créditos de los componentes que se descargan después.
Source: "licencias\*"; DestDir: "{app}\licencias"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\licencias"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\TERCEROS.md"; DestDir: "{app}\licencias"; Flags: ignoreversion
Source: "..\TERCEROS.en.md"; DestDir: "{app}\licencias"; Flags: ignoreversion

[Icons]
Name: "{group}\{#Nombre}"; Filename: "{app}\{#Exe}"
Name: "{autodesktop}\{#Nombre}"; Filename: "{app}\{#Exe}"; Tasks: escritorio

[Run]
; Calíope se abre siempre como el usuario normal, nunca con los permisos de administrador del instalador (al
; instalar para todos los usuarios o al actualizarse sola): como administrador, Windows no deja a los motores usar
; los enlaces de Python creados por el usuario y no arrancan.
Filename: "{app}\{#Exe}"; Description: "{cm:Abrir}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\{#Exe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallDelete]
; Restos que la aplicación crea junto al ejecutable (caché de Python del motor).
Type: filesandordirs; Name: "{app}\motores"

[Code]
// Qué hacer con la carpeta de datos al desinstalar: 0 conservar, 1 borrar modelos y motores, 2 borrar todo.
var
  OpcionBorrado: Integer;

// Deshace los escapes de una cadena JSON (System.Text.Json escribe \\ y \uXXXX para lo que no es ASCII).
function DecodificarJson(const S: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  I := 1;
  while I <= Length(S) do
  begin
    C := S[I];
    if (C = '\') and (I < Length(S)) then
    begin
      I := I + 1;
      C := S[I];
      if C = 'u' then
      begin
        Result := Result + Chr(StrToInt('$' + Copy(S, I + 1, 4)));
        I := I + 4;
      end
      else
        Result := Result + C;
    end
    else
      Result := Result + C;
    I := I + 1;
  end;
end;

// /CARPETADATOS=… (para pruebas) indica otra carpeta y no toca los ajustes del usuario.
function CarpetaForzada: String;
begin
  Result := ExpandConstant('{param:CARPETADATOS|}');
end;

// La carpeta de datos que eligió el usuario (ajustes.json de Calíope) o la de por defecto.
function CarpetaDatos: String;
var
  Json: AnsiString;
  Texto: String;
  P, Q: Integer;
begin
  if CarpetaForzada <> '' then
  begin
    Result := CarpetaForzada;
    Exit;
  end;
  Result := ExpandConstant('{localappdata}\Caliope');
  if not LoadStringFromFile(ExpandConstant('{localappdata}\Caliope\ajustes.json'), Json) then
    Exit;
  Texto := String(Json);
  P := Pos('"carpetaDatos"', Texto);
  if P = 0 then
    Exit;
  Texto := Copy(Texto, P + 14, Length(Texto));
  P := Pos(':', Texto);
  Texto := Trim(Copy(Texto, P + 1, Length(Texto)));
  if (Length(Texto) = 0) or (Texto[1] <> '"') then
    Exit;
  Texto := Copy(Texto, 2, Length(Texto));
  Q := 1;
  while (Q <= Length(Texto)) and not ((Texto[Q] = '"') and ((Q = 1) or (Texto[Q - 1] <> '\'))) do
    Q := Q + 1;
  if Q > 1 then
    Result := DecodificarJson(Copy(Texto, 1, Q - 1));
end;

// Ventana con las tres opciones. Devuelve -1 si se cancela la desinstalación.
function PreguntarBorrado(const Carpeta: String): Integer;
var
  Ventana: TSetupForm;
  Pregunta, Donde, Aviso: TNewStaticText;
  Conservar, Modelos, Todo: TNewRadioButton;
  Aceptar, Cancelar: TNewButton;
begin
  Ventana := CreateCustomForm(ScaleX(500), ScaleY(250), False, False);
  try
    Ventana.Caption := 'Calíope';

    Pregunta := TNewStaticText.Create(Ventana);
    Pregunta.Parent := Ventana;
    Pregunta.Left := ScaleX(16);
    Pregunta.Top := ScaleY(16);
    Pregunta.Font.Style := [fsBold];
    Pregunta.Caption := CustomMessage('Pregunta');

    Donde := TNewStaticText.Create(Ventana);
    Donde.Parent := Ventana;
    Donde.Left := ScaleX(16);
    Donde.Top := ScaleY(38);
    Donde.Caption := FmtMessage(CustomMessage('Carpeta'), [Carpeta]);

    Conservar := TNewRadioButton.Create(Ventana);
    Conservar.Parent := Ventana;
    Conservar.Left := ScaleX(16);
    Conservar.Top := ScaleY(70);
    Conservar.Width := Ventana.ClientWidth - ScaleX(32);
    Conservar.Caption := CustomMessage('Conservar');
    Conservar.Checked := True;

    Modelos := TNewRadioButton.Create(Ventana);
    Modelos.Parent := Ventana;
    Modelos.Left := ScaleX(16);
    Modelos.Top := ScaleY(96);
    Modelos.Width := Ventana.ClientWidth - ScaleX(32);
    Modelos.Caption := CustomMessage('BorrarModelos');

    Todo := TNewRadioButton.Create(Ventana);
    Todo.Parent := Ventana;
    Todo.Left := ScaleX(16);
    Todo.Top := ScaleY(122);
    Todo.Width := Ventana.ClientWidth - ScaleX(32);
    Todo.Caption := CustomMessage('BorrarTodo');

    Aviso := TNewStaticText.Create(Ventana);
    Aviso.Parent := Ventana;
    Aviso.Left := ScaleX(16);
    Aviso.Top := ScaleY(156);
    Aviso.Width := Ventana.ClientWidth - ScaleX(32);
    Aviso.AutoSize := False;
    Aviso.Height := ScaleY(34);
    Aviso.WordWrap := True;
    Aviso.Caption := CustomMessage('Aviso');

    Aceptar := TNewButton.Create(Ventana);
    Aceptar.Parent := Ventana;
    Aceptar.Width := ScaleX(100);
    Aceptar.Height := ScaleY(26);
    Aceptar.Left := Ventana.ClientWidth - ScaleX(16 + 100 + 8 + 100);
    Aceptar.Top := Ventana.ClientHeight - ScaleY(16 + 26);
    Aceptar.Caption := CustomMessage('Continuar');
    Aceptar.ModalResult := mrOk;
    Aceptar.Default := True;

    Cancelar := TNewButton.Create(Ventana);
    Cancelar.Parent := Ventana;
    Cancelar.Width := ScaleX(100);
    Cancelar.Height := ScaleY(26);
    Cancelar.Left := Ventana.ClientWidth - ScaleX(16 + 100);
    Cancelar.Top := Ventana.ClientHeight - ScaleY(16 + 26);
    Cancelar.Caption := CustomMessage('Cancelar');
    Cancelar.ModalResult := mrCancel;
    Cancelar.Cancel := True;

    Ventana.ActiveControl := Aceptar;
    if Ventana.ShowModal <> mrOk then
      Result := -1
    else if Todo.Checked then
      Result := 2
    else if Modelos.Checked then
      Result := 1
    else
      Result := 0;
  finally
    Ventana.Free;
  end;
end;

function InitializeUninstall: Boolean;
begin
  OpcionBorrado := 0;
  Result := True;
  // /BORRARDATOS=0|1|2 decide sin preguntar (pruebas y desinstalación desatendida).
  OpcionBorrado := StrToIntDef(ExpandConstant('{param:BORRARDATOS|-1}'), -1);
  if (OpcionBorrado >= 0) and (OpcionBorrado <= 2) then
    Exit;
  OpcionBorrado := 0;
  // Desinstalando en silencio se conserva todo.
  if not UninstallSilent then
  begin
    OpcionBorrado := PreguntarBorrado(CarpetaDatos);
    Result := OpcionBorrado >= 0;
  end;
end;

procedure Borrar(const Carpeta: String);
begin
  if DirExists(Carpeta) then
    DelTree(Carpeta, True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  D: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  D := CarpetaDatos;
  if OpcionBorrado >= 1 then
  begin
    // Estructura actual y la de versiones anteriores (todo suelto en la raíz).
    Borrar(D + '\Modelos de voz');
    Borrar(D + '\Programas internos');
    Borrar(D + '\modelos');
    Borrar(D + '\herramientas');
    Borrar(D + '\python');
    Borrar(D + '\entornos');
    Borrar(D + '\cache');
    Borrar(D + '\descargas');
    Borrar(D + '\tmp');
    Borrar(D + '\registros');
    DeleteFile(D + '\estado.json');
  end;
  if OpcionBorrado = 2 then
  begin
    Borrar(D + '\Mis voces');
    Borrar(D + '\Mis narraciones');
    Borrar(D + '\Mis sonidos');
    Borrar(D + '\voces');
    Borrar(D + '\proyectos');
    DeleteFile(D + '\LÉEME.txt');
    RemoveDir(D); // solo si ha quedado vacía: lo que no sea de Calíope no se toca
    if CarpetaForzada = '' then
    begin
      DeleteFile(ExpandConstant('{localappdata}\Caliope\ajustes.json'));
      RemoveDir(ExpandConstant('{localappdata}\Caliope'));
    end;
  end;
  if UninstallSilent then
    Exit;
  case OpcionBorrado of
    0: MsgBox(FmtMessage(CustomMessage('Datos'), [D]), mbInformation, MB_OK);
    1: MsgBox(FmtMessage(CustomMessage('Borrado'), [D]), mbInformation, MB_OK);
    2: MsgBox(FmtMessage(CustomMessage('BorradoTodo'), [D]), mbInformation, MB_OK);
  end;
end;
