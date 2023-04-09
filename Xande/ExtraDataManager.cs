using Lumina.Models.Models;
using SharpGLTF.IO;

namespace Xande;

public class ExtraDataManager {
    private readonly Dictionary< string, object > _extraData = new();

    public ExtraDataManager() { }

    public void AddShapeNames( IEnumerable< Shape > shapes ) {
        _extraData.Add("targetNames", shapes.Select( s => s.Name  ).ToArray() );
    }
    public JsonContent Serialize() => JsonContent.CreateFrom( _extraData );
}