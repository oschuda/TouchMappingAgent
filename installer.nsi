; Without this, NSIS reads the script as the system ANSI code page instead of UTF-8. This file is
; saved as UTF-8 and contains German umlauts throughout the wizard page text plus the © in the
; copyright line below; under ACP those multi-byte UTF-8 sequences get split into two separate
; mis-mapped Latin-1 characters each (e.g. "ausführen" becomes "ausfÃ¼hren"). Confirmed by
; inspecting the compiled installer's own file properties, where "©" came out as "Â©".
Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

Name "TouchMappingAgent"
OutFile "TouchMappingAgent-Setup.exe"
InstallDir "$PROGRAMFILES64\PadaLuma\TouchMappingAgent"
RequestExecutionLevel admin

!define STARTMENU_DIR "$SMPROGRAMS\TouchMappingAgent"
!define UNINSTALL_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TouchMappingAgent"

; Single source of truth is Directory.Build.props' VersionPrefix, which also drives the compiled
; assemblies' own AssemblyVersion/FileVersion. build.ps1 reads it and passes it here via
; "/DPRODUCT_VERSION=x.y.z.w" so the installer's file-version resource and the Add/Remove Programs
; entry always match what's actually inside the .exe files it packages. This !ifndef default only
; applies when compiling installer.nsi directly (bypassing build.ps1) and should be bumped by hand
; if that ever drifts noticeably far from Directory.Build.props.
!ifndef PRODUCT_VERSION
    !define PRODUCT_VERSION "1.0.0.0"
!endif
!define COMPANY_NAME "PadaLuma Inkjet Solutions"
!define COPYRIGHT_LINE "© 2026 PadaLuma Inkjet Solutions, Erik Denzler"

; Installer- und Uninstaller-Icon
!define MUI_ICON "assets\touchmappingagent.ico"
!define MUI_UNICON "assets\touchmappingagent.ico"

; File properties of the setup executable itself (Explorer > Properties > Details), and the
; company/copyright line an administrator sees before ever running it.
VIProductVersion "${PRODUCT_VERSION}"
VIFileVersion "${PRODUCT_VERSION}"
VIAddVersionKey "ProductName" "TouchMappingAgent"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileDescription" "TouchMappingAgent Setup"
VIAddVersionKey "CompanyName" "${COMPANY_NAME}"
VIAddVersionKey "LegalCopyright" "${COPYRIGHT_LINE}"

; Branding auf den Assistentenseiten. Beide Bitmaps sind 24-Bit ohne Alpha — MUI2 lehnt
; 32-Bit-BMPs mit Alphakanal ab.
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "assets\installer-header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "assets\installer-header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP "assets\installer-welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "assets\installer-welcome.bmp"

; Finish page: offer the guided commissioning wizard.
;
; Unchecked by default. The wizard writes EDID overrides and runs tabcal — that is a
; commissioning action, not part of an installation, and it must be a deliberate choice by the
; technician standing in front of the machine rather than something that happens because they
; clicked through the installer.
!define MUI_FINISHPAGE_RUN "$INSTDIR\TouchMappingAgent.WPF.exe"
!define MUI_FINISHPAGE_RUN_PARAMETERS "--wizard"
!define MUI_FINISHPAGE_RUN_TEXT "Setup-Assistent nach Installation ausführen"
!define MUI_FINISHPAGE_RUN_NOTCHECKED

; Second finish-page action, checked by default: a plain, ordinary launch of the resident agent
; (no --wizard, no --silent) so the operator sees the main window immediately after closing the
; installer. MUI2's finish page allows exactly one MUI_FINISHPAGE_RUN checkbox, so a second one is
; added the standard way — repurposing the "show readme" slot to call a function instead of
; opening a file.
;
; This does not race the unconditional "Exec ... --silent" at the end of the Install section: that
; call starts the resident, windowless instance; if this checkbox is also ticked, the single-
; instance guard hands the plain launch's intent to that already-running instance, which then
; raises its main window — exactly the "start it and show me something" the checkbox promises.
!define MUI_FINISHPAGE_SHOWREADME ""
!define MUI_FINISHPAGE_SHOWREADME_TEXT "TouchMappingAgent jetzt starten"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION LaunchAgentNow

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

; Finish-page "TouchMappingAgent jetzt starten" action. A plain launch — see the define above
; for why this is safe alongside the unconditional silent autostart at the end of Install.
Function LaunchAgentNow
    Exec '"$INSTDIR\TouchMappingAgent.WPF.exe"'
FunctionEnd

; Waits (up to 10 s) until no process with the given image name is left. taskkill returns as
; soon as termination is REQUESTED; the image stays mapped — and its .exe locked — until the
; process has actually gone. Without this wait, version 1.0.1's silent install skipped
; TouchMappingAgent.WPF.exe without any message and left the old client binary in place.
!macro WaitForProcessExit IMAGE
    StrCpy $R9 0
    ${Do}
        nsExec::ExecToStack 'cmd.exe /c tasklist /FI "IMAGENAME eq ${IMAGE}" /NH | find /I "${IMAGE}"'
        Pop $R8
        ${If} $R8 != 0
            ${Break}
        ${EndIf}
        IntOp $R9 $R9 + 1
        ${If} $R9 >= 20
            ${Break}
        ${EndIf}
        Sleep 500
    ${Loop}
!macroend

Section "Install"
    ; 64-bit registry view throughout. NSIS is a 32-bit program; without this every HKLM\SOFTWARE
    ; access is redirected to WOW6432Node — which is where 1.0.1 put its uninstall entry and Run
    ; value, and why its uninstaller never reached the Mappings key the 64-bit service uses.
    SetRegView 64

    ; Upgrade path: if a previous installation is registered, run ITS uninstaller silently
    ; before touching anything. This is what actually removes stale DLLs, the "en\" satellite
    ; folder and old registry entries from a version whose file set no longer matches this
    ; one — the taskkill/sc.exe steps below only stop processes, they do not clean up files an
    ; older release wrote that this release no longer ships.
    ;
    ; "_?=$INSTDIR" is the standard NSIS idiom for calling an uninstaller synchronously in
    ; place: without it, uninstall.exe copies itself to a temp file and returns immediately so
    ; it can delete itself, and this installer would then race it and start copying new files
    ; into a directory the old uninstaller is still cleaning up.
    ;
    ; "/UPGRADE" tells the uninstaller to KEEP the learned assignments: they describe this
    ; machine's cabling, and an update must not force the operator to teach the screens again.
    ; (1.0.1's uninstaller ignores the switch, but it only ever reached the WOW6432Node copy of
    ; the Mappings key, so it never deleted the real one either.)
    ;
    ; The previous version may have registered itself in either view: 1.0.1 and earlier in
    ; WOW6432Node, 1.0.2 and later in the 64-bit view.
    ReadRegStr $R0 HKLM "${UNINSTALL_REG_KEY}" "UninstallString"
    ${If} $R0 == ""
        SetRegView 32
        ReadRegStr $R0 HKLM "${UNINSTALL_REG_KEY}" "UninstallString"
        SetRegView 64
    ${EndIf}
    ${If} $R0 != ""
        ExecWait '"$R0" /S /UPGRADE _?=$INSTDIR'
    ${EndIf}

    ; Leftovers of the 32-bit registration (1.0.1 and earlier). Removed explicitly because an
    ; older uninstaller may not have run, and a stale Run value would start the client twice.
    SetRegView 32
    DeleteRegKey HKLM "${UNINSTALL_REG_KEY}"
    DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "TouchMappingAgent"
    DeleteRegKey HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\TouchMappingAgent"
    SetRegView 64

    ; Stop and remove any previous installation first. Without this, re-running the
    ; installer over an already-running service leaves its .exe/.dll files locked, so the
    ; File instructions below silently fail to update them and the OLD (possibly broken)
    ; service binary keeps running after "successful" reinstall. Kept as a safety net even
    ; after the silent-uninstall step above, in case the registered UninstallString points at
    ; a version built before that step's own cleanup was complete.
    nsExec::ExecToLog 'net stop TouchMappingAgent'
    nsExec::ExecToLog 'sc.exe delete TouchMappingAgent'

    ; Safety net: a previously-installed service built before the RunServiceAsync shutdown
    ; fix (linking its CancellationTokenSource to IHostApplicationLifetime.ApplicationStopping)
    ; never actually exits on "net stop" — it hangs forever holding its own .exe file open,
    ; which surfaced as "Error opening file for writing: ...TouchMappingAgent.Service.exe"
    ; during install. Force-kill it so the File instruction below can actually overwrite it.
    nsExec::ExecToLog 'taskkill /IM TouchMappingAgent.Service.exe /F'
    !insertmacro WaitForProcessExit "TouchMappingAgent.Service.exe"

    ; The WPF tray client is normally still running (minimized to tray) from a previous
    ; install — its .exe is just as locked as the service's while it's running, and
    ; overwriting a locked file silently fails. Without this, a re-run of the installer can
    ; update the service but leave the OLD client binary in place and running, which looks
    ; exactly like "nothing changed" even though the service-side fix did apply.
    nsExec::ExecToLog 'taskkill /IM TouchMappingAgent.WPF.exe /F'
    !insertmacro WaitForProcessExit "TouchMappingAgent.WPF.exe"

    SetOutPath "$INSTDIR"
    File "publish\*.exe"
    File "publish\*.dll"
    File "publish\*.json"
    File "assets\touchmappingagent.ico"

    ; Satellite assemblies — the translated UI text. These live in per-culture SUBDIRECTORIES,
    ; so the flat "publish\*.dll" above does not pick them up. Without this block the product
    ; installs with German only and every other language silently falls back to it, which looks
    ; exactly like the translation never having been written.
    ;
    ; German is deliberately absent here: it is the neutral culture compiled into
    ; TouchMappingAgent.WPF.dll itself (see AssemblyInfo.cs), so it ships with the main assembly
    ; and cannot go missing.
    ;
    ; NOTE: adding a language means adding its folder here as well as its .resx.
    SetOutPath "$INSTDIR\en"
    File "publish\en\*.resources.dll"
    SetOutPath "$INSTDIR"

    ; Register EventLog
    WriteRegStr HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\TouchMappingAgent" "EventMessageFile" "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll"
    WriteRegDWORD HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\TouchMappingAgent" "TypesSupported" 7

    ; Install Service
    nsExec::ExecToLog 'sc.exe create TouchMappingAgent binPath= "$INSTDIR\TouchMappingAgent.Service.exe" start= auto'
    nsExec::ExecToLog 'net start TouchMappingAgent'

    ; Start Menu shortcut (all users)
    CreateDirectory "${STARTMENU_DIR}"
    CreateShortCut "${STARTMENU_DIR}\TouchMappingAgent.lnk" "$INSTDIR\TouchMappingAgent.WPF.exe" "" "$INSTDIR\touchmappingagent.ico"
    CreateShortCut "${STARTMENU_DIR}\Uninstall TouchMappingAgent.lnk" "$INSTDIR\uninstall.exe"

    ; Desktop shortcut (all users)
    CreateShortCut "$DESKTOP\TouchMappingAgent.lnk" "$INSTDIR\TouchMappingAgent.WPF.exe" "" "$INSTDIR\touchmappingagent.ico"

    ; Autostart, headless. The agent MUST be resident: the background service decides which
    ; stored assignments need re-applying, but it runs in Session 0 and can neither see the
    ; desktop nor execute tabcal.exe. Only this client, in the interactive session, can. With
    ; it not running, a reboot or a DisplayPort event leaves touch input on the wrong screen.
    ; "--silent" starts it into the tray with no window.
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "TouchMappingAgent" \
        '"$INSTDIR\TouchMappingAgent.WPF.exe" --silent'

    WriteUninstaller "$INSTDIR\uninstall.exe"
    WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "DisplayName" "TouchMappingAgent"
    WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "UninstallString" "$INSTDIR\uninstall.exe"
    WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "Publisher" "${COMPANY_NAME}"
    WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "DisplayIcon" "$INSTDIR\touchmappingagent.ico"

    ; Launch the resident agent immediately, headless. It must be running for stored
    ; assignments to be re-applied, so this is unconditional rather than an opt-in checkbox.
    ;
    ; "--silent" and not a plain launch: the finish page below optionally starts the same
    ; executable with "--wizard". The agent enforces one instance per session, so that second
    ; launch hands its intent to this one and exits — but only if this one is the resident,
    ; windowless instance. Starting it with a window here would put two agent windows on screen
    ; for anyone who ticks the box.
    ;
    ; Note: since the installer itself runs elevated (RequestExecutionLevel admin), this first
    ; launch inherits that elevated token; every subsequent launch (autostart, Start Menu or
    ; Desktop shortcut) runs at the user's normal, non-elevated level.
    Exec '"$INSTDIR\TouchMappingAgent.WPF.exe" --silent'
SectionEnd

Section "Uninstall"
    ; See the Install section: without this, HKLM\SOFTWARE is the WOW6432Node copy.
    SetRegView 64

    nsExec::ExecToLog 'net stop TouchMappingAgent'
    nsExec::ExecToLog 'sc.exe delete TouchMappingAgent'

    ; Stop the client too: a running client keeps its .exe locked, and the Delete below would
    ; leave it behind.
    nsExec::ExecToLog 'taskkill /IM TouchMappingAgent.WPF.exe /F'
    !insertmacro WaitForProcessExit "TouchMappingAgent.WPF.exe"

    ; Remove shortcuts
    Delete "$DESKTOP\TouchMappingAgent.lnk"
    Delete "${STARTMENU_DIR}\TouchMappingAgent.lnk"
    Delete "${STARTMENU_DIR}\Uninstall TouchMappingAgent.lnk"
    RMDir "${STARTMENU_DIR}"

    Delete "$INSTDIR\*.exe"
    Delete "$INSTDIR\*.dll"
    Delete "$INSTDIR\*.json"
    Delete "$INSTDIR\touchmappingagent.ico"

    ; Satellite assemblies. RMDir without /r on purpose: it removes the folder only when empty,
    ; so anything an administrator put there by hand survives instead of being deleted silently.
    Delete "$INSTDIR\en\*.resources.dll"
    RMDir "$INSTDIR\en"

    RMDir "$INSTDIR"

    ; Per-user UI language choice. HKCU and not HKLM because the language belongs to the person
    ; at the terminal, not the machine — see Localizer. Only the uninstalling user's value is
    ; reachable from here; others are harmless leftovers pointing at a key nothing reads.
    DeleteRegValue HKCU "Software\PadaLuma\TouchMappingAgent" "Language"

    DeleteRegKey HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\TouchMappingAgent"
    DeleteRegKey HKLM "${UNINSTALL_REG_KEY}"
    DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "TouchMappingAgent"

    ; Learned touch-to-monitor assignments. Removed on a real uninstall because they describe
    ; THIS machine's cabling and are meaningless without the agent that applies them — but kept
    ; when the installer of a newer version runs this uninstaller as part of an upgrade.
    ${GetParameters} $R0
    ClearErrors
    ${GetOptions} $R0 "/UPGRADE" $R1
    ${If} ${Errors}
        DeleteRegKey HKLM "SOFTWARE\PadaLuma\TouchMappingAgent\Mappings"
    ${EndIf}
SectionEnd
