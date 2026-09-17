// This project enables both UseWPF and UseWindowsForms: WPF for the interface, Windows
// Forms solely for NotifyIcon, which WPF has no equivalent for. ImplicitUsings therefore
// imports System.Drawing and System.Windows.Forms globally, and a dozen type names become
// ambiguous because both stacks define their own.
//
// Only names this project always means in the WPF sense are aliased here. Names that are
// genuinely wanted from System.Drawing somewhere -- Color, Brush, Point, Size -- are
// deliberately left alone, because a global alias cannot be overridden by a file-level one
// (they collide rather than shadow). Those are disambiguated per file instead.

global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using MessageBox = System.Windows.MessageBox;
global using UserControl = System.Windows.Controls.UserControl;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
