Option Explicit

Dim shell, fileSystem, projectDirectory
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

projectDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = projectDirectory
shell.Run "cmd.exe /c npm start", 0, False
