; KeidoScreen installer (NSIS 3, Unicode)
; makensis /DVERSION=1.0.0 /DDIST=<staged app dir> /DOUTDIR=<output dir> installer.nsi
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!define APP "KeidoScreen"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"

Name "${APP}"
OutFile "${OUTDIR}\${APP}-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP}"
InstallDirRegKey HKLM "Software\${APP}" "InstallDir"
RequestExecutionLevel admin
BrandingText "${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "${APP}"
VIAddVersionKey /LANG=0 "FileDescription" "${APP} Setup"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "LegalCopyright" "GPL-3.0"

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!define MUI_ICON "${DIST}\KeidoScreen.ico"
!define MUI_UNICON "${DIST}\KeidoScreen.ico"
!define MUI_ABORTWARNING
; start the app as the normal user, not elevated (explorer.exe trick)
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${DIST}\licenses\LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

LangString MsgOs ${LANG_RUSSIAN} "Для ${APP} нужна 64-битная Windows 10 или 11."
LangString MsgOs ${LANG_ENGLISH} "${APP} requires 64-bit Windows 10 or 11."

Function .onInit
  ${IfNot} ${RunningX64}
  ${OrIfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP $(MsgOs)
    Abort
  ${EndIf}
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

Function LaunchApp
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APP}.exe"'
FunctionEnd

; silent auto-update from the app: Setup.exe /S /RUN  => restart KeidoScreen afterwards
Function .onInstSuccess
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/RUN" $1
  ${IfNot} ${Errors}
    Call LaunchApp
  ${EndIf}
FunctionEnd

!macro StopApp
  nsExec::Exec 'taskkill /f /im ${APP}.exe'
  nsExec::Exec 'taskkill /f /im uxplay.exe'
  Sleep 500
!macroend

Section "Install"
  !insertmacro StopApp
  SetOutPath "$INSTDIR"
  RMDir /r "$INSTDIR\engine"   ; drop files of an older version
  File "${DIST}\${APP}.exe"
  File "${DIST}\KeidoScreen.ico"
  File /r "${DIST}\engine"
  File /r "${DIST}\licenses"

  ; inbound rule for the engine, limited to the local network
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP}"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${APP}" dir=in action=allow program="$INSTDIR\engine\bin\uxplay.exe" enable=yes profile=any remoteip=localsubnet'

  SetShellVarContext all
  CreateDirectory "$SMPROGRAMS\${APP}"
  CreateShortcut "$SMPROGRAMS\${APP}\${APP}.lnk" "$INSTDIR\${APP}.exe"
  CreateShortcut "$SMPROGRAMS\${APP}\Удалить ${APP}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${APP}.exe"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\${APP}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP}.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${APP}"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" $0
SectionEnd

Section "Uninstall"
  !insertmacro StopApp
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP}"'
  SetShellVarContext all
  Delete "$DESKTOP\${APP}.lnk"
  RMDir /r "$SMPROGRAMS\${APP}"
  ; autostart entry of the current user (set from the app's settings)
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP}"
  RMDir /r "$INSTDIR\engine"
  RMDir /r "$INSTDIR\licenses"
  Delete "$INSTDIR\${APP}.exe"
  Delete "$INSTDIR\KeidoScreen.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "Software\${APP}"
SectionEnd
