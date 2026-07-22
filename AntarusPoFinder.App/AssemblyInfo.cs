using System.Runtime.CompilerServices;
using System.Windows;

// Lets AntarusPoFinder.Tests reach a handful of internal test-only seams (currently
// AppUpdateService.SetHttpClientForTests/ResetHttpClientForTests) without making them public API.
[assembly: InternalsVisibleTo("AntarusPoFinder.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
