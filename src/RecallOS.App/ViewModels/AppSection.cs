namespace RecallOS.App.ViewModels;

/// <summary>
/// The panes the sidebar switches between.
/// </summary>
/// <remarks>
/// The window is a shell with one content area rather than a single dense screen. A first
/// run lands on <see cref="Home"/>, which explains what to do next; an experienced user
/// goes straight to <see cref="Search"/>. Without this split the app opened on an empty
/// result list, which told a new user nothing at all.
/// </remarks>
public enum AppSection
{
    Home,
    Search,
    Timeline
}
