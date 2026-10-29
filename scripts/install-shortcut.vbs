'---------------------------------------------------------------------------------------------------------
' Mediator: creates "Mediator" shortcuts on the Desktop and in the Start menu for Mediator.exe in this folder.
' Double-click it. Per-user only (no administrator rights), and no PowerShell execution policy involved.
'   install-shortcut.vbs                 Desktop + Start menu, then offers to start Mediator
'   install-shortcut.vbs /nostartmenu    Desktop only
'   install-shortcut.vbs /quiet          no dialogs (for scripts)
' Run on Windows startup is a setting inside the app (setup card), so it can be turned off there.
' Published into dist\Mediator\ by scripts\publish-release.ps1. Create-Desktop-Shortcut.ps1 does the same in PowerShell.
'---------------------------------------------------------------------------------------------------------
Option Explicit

Dim shell, fso, here, target, startMenu, quiet, i, places, folder, link, linkPath, made

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
target = fso.BuildPath(here, "Mediator.exe")

startMenu = True
quiet = False
For i = 0 To WScript.Arguments.Count - 1
    Select Case LCase(WScript.Arguments(i))
        Case "/nostartmenu": startMenu = False
        Case "/quiet": quiet = True
    End Select
Next

If Not fso.FileExists(target) Then
    If Not quiet Then MsgBox "Mediator.exe was not found next to this script:" & vbCrLf & here, vbExclamation, "Mediator"
    WScript.Quit 1
End If

If startMenu Then
    places = Array(shell.SpecialFolders("Desktop"), shell.SpecialFolders("Programs"))
Else
    places = Array(shell.SpecialFolders("Desktop"))
End If

made = ""
For Each folder In places
    If Len(folder) > 0 Then
        If fso.FolderExists(folder) Then
            linkPath = fso.BuildPath(folder, "Mediator.lnk")
            Set link = shell.CreateShortcut(linkPath)
            link.TargetPath = target
            link.WorkingDirectory = here
            link.IconLocation = target & ",0"
            link.Description = "Mediator: Ctrl+Shift+Space opens the panel"
            link.Save
            made = made & vbCrLf & "  " & linkPath
        End If
    End If
Next

If quiet Then WScript.Quit 0

If MsgBox("Shortcuts created:" & made & vbCrLf & vbCrLf & "Start Mediator now?", vbYesNo + vbInformation, "Mediator") = vbYes Then
    shell.CurrentDirectory = here
    shell.Run """" & target & """", 1, False
End If
