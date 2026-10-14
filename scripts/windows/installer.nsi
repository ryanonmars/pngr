; The wizard owns presentation only. Velopack owns installation, shortcuts,
; uninstall registration and updates, retaining the original pngr package ID.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

Name "Pngr"
OutFile "${OUTPUT}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\pngr"
SetCompressor /SOLID lzma
Icon "${ICON}"
VIProductVersion "${NUMERIC_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "Pngr"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileDescription" "Pngr Setup"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Pngr contributors"
BrandingText "Pngr"
ShowInstDetails show
!define MUI_ABORTWARNING
!define MUI_ICON "${ICON}"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "${HEADER_BITMAP}"
!define MUI_WELCOMEFINISHPAGE_BITMAP "${WELCOME_BITMAP}"
!define MUI_WELCOMEPAGE_TITLE "Welcome to Pngr Setup"
!define MUI_WELCOMEPAGE_TEXT "This wizard installs Pngr for your Windows account. No administrator access is required.$\r$\n$\r$\nPngr manages application updates and uninstall support through Velopack. Your monitoring history and settings are preserved.$\r$\n$\r$\nClose Pngr before continuing."
!define MUI_FINISHPAGE_TITLE "Pngr installation complete"
!define MUI_FINISHPAGE_TEXT "Pngr is ready. Use the Start menu shortcut to open it.$\r$\n$\r$\nTo remove Pngr, use Windows Installed apps."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Launch Pngr"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchPngr
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Section "Install Pngr"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=VelopackSetup.exe "${PAYLOAD}"
    DetailPrint "Installing Pngr for your account…"
    ; Silent also suppresses Velopack's first-launch behavior. The finish page
    ; supplies an explicit launch choice, and /S installations never launch.
    ClearErrors
    ExecWait '"$PLUGINSDIR\VelopackSetup.exe" --silent' $0
    ${If} ${Errors}
        SetErrorLevel 1
        MessageBox MB_ICONSTOP|MB_OK "Pngr Setup could not start its installation engine. Please download the installer again." /SD IDOK
        Abort
    ${EndIf}
    ${If} $0 != 0
        SetErrorLevel $0
        MessageBox MB_ICONSTOP|MB_OK "Pngr could not be installed (error $0). Your existing installation has not been removed. Close Pngr and try again." /SD IDOK
        Abort
    ${EndIf}
    ${IfNot} ${FileExists} "$INSTDIR\Update.exe"
        SetErrorLevel 1
        MessageBox MB_ICONSTOP|MB_OK "Pngr installation did not produce its update manager. Please try installing again." /SD IDOK
        Abort
    ${EndIf}
    SetErrorLevel 0
SectionEnd

Function LaunchPngr
    Exec '"$INSTDIR\Update.exe" start pngr.exe'
FunctionEnd
