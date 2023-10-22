namespace Xande.TestPlugin.Models;

public class ResourceTree {
    public string Name { get; set; } = null!;
    public Node[] Nodes { get; set; } = null!;
    //[J( "CustomizeData" )] public CustomizeData CustomizeData { get; set; }
    public long RaceCode { get; set; }
}

/*public partial class CustomizeData {
    public long Race { get; set; }
    public long Sex { get; set; }
    public long BodyType { get; set; }
    public long Clan { get; set; }
    public long LipColorFurPattern { get; set; }
}*/