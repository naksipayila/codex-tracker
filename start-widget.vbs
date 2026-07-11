Option Explicit

Dim shell, fileSystem, projectDirectory, installExitCode
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

projectDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = projectDirectory

If Not fileSystem.FileExists(projectDirectory & "\node_modules\electron\dist\electron.exe") Then
  ' Keep the first-run install visible so npm errors are actionable.
  installExitCode = shell.Run("cmd.exe /d /c npm install", 1, True)
  If installExitCode <> 0 Then
    MsgBox "Dependencies could not be installed. Open a terminal in this folder and run npm install.", 16, "Codex Usage Tray"
    WScript.Quit installExitCode
  End If
End If

shell.Run "cmd.exe /c npm start", 0, False
