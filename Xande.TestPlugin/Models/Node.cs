namespace Xande.TestPlugin.Models;

public class Node {
    public string Name { get; set; } = null!;
    public string FallbackName { get; set; } = null!;
    public string DisplayName => string.IsNullOrWhiteSpace( Name ) ? FallbackName : Name;
    public long Type { get; set; }

    // Maybe use to cover some weird cases?
    //[J( "PossibleGamePaths" )] public string[] PossibleGamePaths { get; set; }

    // custom setter to replace \\ in non-rooted paths with /
    private string _fullPath = null!;
    public string FullPath { set => _fullPath = Path.IsPathRooted( value ) ? value : value.Replace( "\\", "/" );
        get => _fullPath; }
    public Node[] Children { get; set; } = null!;
    public string GamePath { get; set; } = null!;
}