# IntentMesh demo GIF — render with: vhs docs/demo.tape  (https://github.com/charmbracelet/vhs)
# Produces docs/img/demo.gif: the indirect-injection defense (--demo 3).

Output docs/img/demo.gif

Set Shell "bash"
Set FontSize 15
Set Width 1180
Set Height 760
Set Padding 18
Set Theme { "name": "IntentMesh", "background": "#0b0f14", "foreground": "#e6edf3", "cursor": "#4cc2ff", "selection": "#1f2a38", "black": "#0b0f14", "red": "#f87171", "green": "#34d399", "yellow": "#fbbf24", "blue": "#4cc2ff", "magenta": "#a78bfa", "cyan": "#67e8f9", "white": "#e6edf3" }

Type "dotnet run --project src/IntentMesh.Cli -- --demo 3"
Enter
Sleep 9s
