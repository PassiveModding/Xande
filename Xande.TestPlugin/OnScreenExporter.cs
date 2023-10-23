using System.Diagnostics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System.Drawing.Imaging;
using System.Drawing;
using System.Text.RegularExpressions;
using Xande.Files;
using Xande.Havok;
using Xande.Models.Export;
using System.Numerics;
using Xande.Models;
using Lumina.Data;
using Xande.TestPlugin.Models;
using Xande.Enums;
using Xande.Lumina.Materials;
using Xande.Lumina.Models;

namespace Xande.TestPlugin {
    public class OnScreenExporter {
        private readonly HavokConverter _converter;
        private readonly LuminaManager  _luminaManager;
        private readonly PbdFile        _pbd;
        private readonly IPluginLog     _log;

        // tbd if this is needed, ran into issues when accessing multiple skeletons in succession
        private readonly Dictionary< string, HavokXml > _skeletonCache = new();

        // prevent multiple exports at once
        private static readonly SemaphoreSlim ExportSemaphore = new(1, 1);

        // only allow one texture to be written at a time due to memory issues
        private static readonly SemaphoreSlim TextureSemaphore = new(1, 1);

        public OnScreenExporter( HavokConverter converter, LuminaManager luminaManager ) {
            _converter     = converter;
            _luminaManager = luminaManager;
            _pbd           = _luminaManager.GetPbdFile();
            _log           = Service.Logger;
        }

        public Task ExportResourceTree( ResourceTree tree, bool[] enabledNodes, bool openFolderWhenComplete, CancellationToken cancellationToken ) {
            if( ExportSemaphore.CurrentCount == 0 ) {
                _log.Warning( "Export already in progress" );
                return Task.CompletedTask;
            }

            var path = Path.Combine( Path.GetTempPath(), "Penumbra.XandeTest" );
            Directory.CreateDirectory( path );
            path = Path.Combine( path, $"{tree.Name}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}" );
            Directory.CreateDirectory( path );

            return Service.Framework.RunOnTick( () => {
                List< Node > nodes = new();
                for( int i = 0; i < enabledNodes.Length; i++ ) {
                    if( enabledNodes[ i ] == false ) continue;
                    var node = tree.Nodes[ i ];
                    nodes.Add( node );
                }

                _log.Debug( $"Exporting character to {path}" );

                // skeletons should only be at the root level so no need to go further
                // do not exclude skeletons regardless of option (because its annoying)
                var skeletonNodes = tree.Nodes.Where( x => x.Type == ( long )Penumbra.Api.Enums.ResourceType.Sklb ).ToList();
                // if skeleton is for weapon, move it to the end
                skeletonNodes.Sort( ( x, y ) => {
                    if( x.GamePath.Contains( "weapon" ) ) { return 1; }

                    if( y.GamePath.Contains( "weapon" ) ) { return -1; }

                    return 0;
                } );

                // will error if not done on the framework thread
                var skeletons = new List< HavokXml >();
                try {
                    foreach( var node in skeletonNodes ) {
                        // cannot use fullpath because things like ivcs are fucky and crash the game
                        var nodePath = node.FullPath;
                        if( _skeletonCache.TryGetValue( nodePath, out var havokXml ) ) {
                            skeletons.Add( havokXml );
                            continue;
                        }

                        try {
                            var file = _luminaManager.GetFile< FileResource >( nodePath );

                            if( file == null ) { throw new Exception( "GetFile returned null" ); }

                            var sklb = SklbFile.FromStream( file.Reader.BaseStream );

                            var xml = _converter.HkxToXml( sklb.HkxData );
                            havokXml = new HavokXml( xml );
                            skeletons.Add( havokXml );
                            _skeletonCache.Add( nodePath, havokXml );
                            _log.Debug( $"Loaded skeleton {nodePath}" );
                            continue;
                        }
                        catch( Exception ex ) { _log.Error( ex, $"Failed to load {nodePath}, falling back to GamePath" ); }

                        nodePath = node.GamePath;
                        if( _skeletonCache.TryGetValue( nodePath, out havokXml ) ) {
                            skeletons.Add( havokXml );
                            continue;
                        }

                        try {
                            var file = _luminaManager.GetFile< FileResource >( nodePath );

                            if( file == null ) { throw new Exception( "GetFile returned null" ); }

                            var sklb = SklbFile.FromStream( file.Reader.BaseStream );

                            var xml = _converter.HkxToXml( sklb.HkxData );
                            havokXml = new HavokXml( xml );
                            skeletons.Add( havokXml );
                            _skeletonCache.Add( nodePath, havokXml );
                            _log.Debug( $"Loaded skeleton {nodePath}" );
                        }
                        catch( Exception ex ) { _log.Error( ex, $"Failed to load {nodePath}" ); }
                    }
                }
                catch( Exception ex ) {
                    _log.Error( ex, "Error loading skeletons" );
                    return Task.CompletedTask;
                }

                return Task.Run( async () => {
                    if( ExportSemaphore.CurrentCount == 0 ) {
                        _log.Warning( "Export already in progress" );
                        return;
                    }

                    await ExportSemaphore.WaitAsync( cancellationToken );
                    try {
                        await ExportModel( path, skeletons, tree, nodes, cancellationToken );
                        // open path
                        if( openFolderWhenComplete ) { Process.Start( "explorer.exe", path ); }
                    }
                    catch( Exception e ) { _log.Error( e, "Error while exporting character" ); }
                    finally { ExportSemaphore.Release(); }
                }, cancellationToken );
            }, cancellationToken: cancellationToken );
        }

        private async Task ExportModel( string exportPath, IEnumerable< HavokXml > skeletons, ResourceTree tree, IEnumerable< Node > nodes,
            CancellationToken cancellationToken = default ) {
            try {
                var genderRace   = ( GenderRace )tree.RaceCode;
                var deform       = ( ushort )genderRace;
                var boneMap      = ModelConverter.GetBoneMap( skeletons.ToArray(), out var root );
                var joints       = boneMap.Values.ToArray();
                var raceDeformer = new RaceDeformer( _pbd, boneMap );
                var modelNodes   = nodes.Where( x => ( Penumbra.Api.Enums.ResourceType )x.Type == Penumbra.Api.Enums.ResourceType.Mdl ).ToArray();
                var glTfScene    = new SceneBuilder( modelNodes.Length > 0 ? modelNodes[ 0 ].GamePath : "scene" );
                if( root != null ) { glTfScene.AddNode( root ); }

                var modelTasks = new List< Task >();
                // chara/human/c1101/obj/body/b0003/model/c1101b0003_top.mdl
                var stupidLowPolyModelRegex = new Regex( @"^chara/human/c\d+/obj/body/b0003/model/c\d+b0003_top.mdl$");
                foreach( var node in modelNodes ) {
                    if( stupidLowPolyModelRegex.IsMatch( node.GamePath ) )
                    {
                        _log.Warning( $"Skipping model {node.FullPath}" );
                        continue;
                    }
                    _log.Debug( $"Handling model {node.FullPath}" );
                    modelTasks.Add( HandleModel( node, raceDeformer, deform, exportPath, boneMap, joints, glTfScene, cancellationToken ) );
                }

                await Task.WhenAll( modelTasks );

                var glTfModel = glTfScene.ToGltf2();
                //var waveFrontFolder = Path.Combine(exportPath, "wavefront");
                //Directory.CreateDirectory(waveFrontFolder);
                //glTFModel.SaveAsWavefront(Path.Combine(waveFrontFolder, "wavefront.obj"));

                var glTfFolder = Path.Combine( exportPath, "gltf" );
                Directory.CreateDirectory( glTfFolder );
                glTfModel.SaveGLTF( Path.Combine( glTfFolder, "gltf.gltf" ) );

                //var glbFolder = Path.Combine(exportPath, "glb");
                //Directory.CreateDirectory(glbFolder);
                //glTFModel.SaveGLB(Path.Combine(glbFolder, "glb.glb"));

                _log.Debug( $"Exported model to {exportPath}" );
            }
            catch( Exception e ) { _log.Error( e, "Failed to export model" ); }
        }

        private async Task HandleModel( Node node, RaceDeformer raceDeformer, ushort? deform, string exportPath, Dictionary< string, NodeBuilder > boneMap, NodeBuilder[] joints,
            SceneBuilder glTfScene, CancellationToken cancellationToken ) {
            var path = node.FullPath;
            //var file = _luminaManager.GetFile<FileResource>(path);
            if( !TryGetModel( node, deform, out var modelPath, out var model ) ) { return; }

            if( model == null ) return;

            if( string.Equals( path, modelPath, StringComparison.InvariantCultureIgnoreCase ) ) { _log.Debug( $"Using full path for {path}" ); }
            else {
                _log.Debug( $"Retrieved model\n" +
                    $"Used path: {modelPath}\n" +
                    $"Init path: {path}" );
            }

            var name     = Path.GetFileNameWithoutExtension( path );
            var raceCode = raceDeformer.RaceCodeFromPath( path );

            // reaper eye go away
            var stupidEyeMeshRegex      = new Regex( @"^/mt_c\d+f.+_etc_b.mtrl$" );
            var meshes = model.Meshes.Where( x => x.Types.Contains( Mesh.MeshType.Main ) &&
                    !stupidEyeMeshRegex.IsMatch( x.Material.MaterialPath.ToString() ) )
                .ToArray();
            var nodeChildren = node.Children.ToList();

            var materials = new List< (string fullpath, string gamepath, MaterialBuilder material) >();

            var textureTasks = new List< Task >();

            foreach( var child in nodeChildren ) {
                textureTasks.Add( Task.Run( () => {
                    var childType = ( Penumbra.Api.Enums.ResourceType )child.Type;
                    if( childType != Penumbra.Api.Enums.ResourceType.Mtrl ) { return; }

                    Material? material;
                    try {
                        var mtrlFile = Path.IsPathRooted( child.FullPath )
                            ? _luminaManager.GameData.GetFileFromDisk< MtrlFile >( child.FullPath, child.GamePath )
                            : _luminaManager.GameData.GetFile< MtrlFile >( child.FullPath );

                        if( mtrlFile == null ) {
                            _log.Warning( $"Could not load material {child.FullPath}" );
                            return;
                        }

                        material = new Material( mtrlFile );
                    }
                    catch( Exception e ) {
                        _log.Error( e, $"Failed to load material {child.FullPath}" );
                        return;
                    }

                    try {
                        var glTfMaterial = ComposeTextures( material, exportPath, child.Children, cancellationToken );

                        if( glTfMaterial == null ) { return; }

                        materials.Add( ( child.FullPath, child.GamePath, glTfMaterial ) );
                    }
                    catch( Exception e ) { _log.Error( e, $"Failed to compose textures for material {child.FullPath}" ); }
                }, cancellationToken ) );
            }

            await Task.WhenAll( textureTasks );

            if( cancellationToken.IsCancellationRequested ) { return; }

            foreach( var mesh in meshes ) { mesh.Material.Update( _luminaManager.GameData ); }

            _log.Debug(
                $"Handling model {name} with {meshes.Length} meshes\n" +
                $"{string.Join( "\n", meshes.Select( x => x.Material.ResolvedPath ) )}\n" +
                $"Using materials\n{string.Join( "\n", materials.Select( x => x.fullpath == x.gamepath ? x.fullpath : $"{x.gamepath} -> {x.fullpath}" ) )}" );

            foreach( var mesh in meshes ) {
                // try get material from materials
                var material = materials.FirstOrDefault( x => x.fullpath == mesh.Material.ResolvedPath || x.gamepath == mesh.Material.ResolvedPath );

                if( material == default ) {
                    // match most similar material from list
                    if( mesh.Material.ResolvedPath == null ) {
                        _log.Warning( $"Could not find material for {mesh.Material.ResolvedPath}" );
                        continue;
                    }

                    var match  = materials.Select( x => ( x.fullpath, x.gamepath, ComputeLd( x.fullpath, mesh.Material.ResolvedPath ) ) ).OrderBy( x => x.Item3 ).FirstOrDefault();
                    var match2 = materials.Select( x => ( x.fullpath, x.gamepath, ComputeLd( x.gamepath, mesh.Material.ResolvedPath ) ) ).OrderBy( x => x.Item3 ).FirstOrDefault();

                    material = match.Item3 < match2.Item3
                        ? materials.FirstOrDefault( x => x.fullpath == match.fullpath || x.gamepath == match.gamepath )
                        : materials.FirstOrDefault( x => x.fullpath == match2.fullpath || x.gamepath == match2.gamepath );
                }

                if( material == default ) {
                    _log.Warning( $"Could not find material for {mesh.Material.ResolvedPath}" );
                    continue;
                }

                try {
                    if( mesh.Material.ResolvedPath != material.gamepath ) { _log.Warning( $"Using material {material.gamepath} for {mesh.Material.ResolvedPath}" ); }

                    await HandleMeshCreation( material.material, raceDeformer, glTfScene, mesh, model, raceCode, deform, boneMap, name, joints );
                }
                catch( Exception e ) { _log.Error( e, $"Failed to handle mesh creation for {mesh.Material.ResolvedPath}" ); }
            }
        }

        private Task HandleMeshCreation( MaterialBuilder glTfMaterial,
            RaceDeformer raceDeformer,
            SceneBuilder glTfScene,
            Mesh xivMesh,
            Model xivModel,
            ushort? raceCode,
            ushort? deform,
            IReadOnlyDictionary< string, NodeBuilder > boneMap,
            string name,
            NodeBuilder[] joints ) {
            var boneSet = xivMesh.BoneTable();
            //var boneSetJoints = boneSet?.Select( n => boneMap[n] ).ToArray();
            var boneSetJoints = boneSet?.Select( n => {
                if( boneMap.TryGetValue( n, out var node ) ) { return node; }

                _log.Warning( $"Could not find bone {n} in boneMap" );
                return null;
            } ).Where( x => x != null ).Select( x => x! ).ToArray();
            var useSkinning = boneSet != null;

            // Mapping between ID referenced in the mesh and in Havok
            Dictionary< int, int > jointIdMapping = new();
            for( var i = 0; i < boneSetJoints?.Length; i++ ) {
                var joint = boneSetJoints[ i ];
                var idx   = joints.ToList().IndexOf( joint );
                jointIdMapping[ i ] = idx;
            }


            // Handle submeshes and the main mesh
            var meshBuilder = new MeshBuilder(
                xivMesh,
                useSkinning,
                jointIdMapping,
                glTfMaterial,
                raceDeformer
            );

            // Deform for full bodies
            if( raceCode != null && deform != null ) {
                _log.Debug( $"Setting up deform steps for {name}, {raceCode.Value}, {deform.Value}" );
                meshBuilder.SetupDeformSteps( raceCode.Value, deform.Value );
            }

            meshBuilder.BuildVertices();

            if( xivMesh.Submeshes.Length > 0 ) {
                for( var i = 0; i < xivMesh.Submeshes.Length; i++ ) {
                    try {
                        var xivSubmesh = xivMesh.Submeshes[ i ];
                        var subMesh    = meshBuilder.BuildSubmesh( xivSubmesh );
                        subMesh.Name = $"{name}_{xivMesh.MeshIndex}.{i}";
                        meshBuilder.BuildShapes( xivModel.Shapes.Values.ToArray(), subMesh, ( int )xivSubmesh.IndexOffset,
                            ( int )( xivSubmesh.IndexOffset + xivSubmesh.IndexNum ) );

                        if( !NodeBuilder.IsValidArmature( joints ) ) {
                            _log.Warning( $"Joints are not valid, skipping submesh {i} for {name}, {string.Join( ", ", joints.Select( x => x.Name ) )}" );
                            continue;
                        }

                        if( useSkinning ) { glTfScene.AddSkinnedMesh( subMesh, Matrix4x4.Identity, joints ); }
                        else { glTfScene.AddRigidMesh( subMesh, Matrix4x4.Identity ); }
                    }
                    catch( Exception e ) { _log.Error( e, $"Failed to build submesh {i} for {name}" ); }
                }
            }
            else {
                var mesh = meshBuilder.BuildMesh();
                mesh.Name = $"{name}_{xivMesh.MeshIndex}";
                _log.Debug( $"Building mesh: \"{mesh.Name}\"" );
                meshBuilder.BuildShapes( xivModel.Shapes.Values.ToArray(), mesh, 0, xivMesh.Indices.Length );
                if( useSkinning ) { glTfScene.AddSkinnedMesh( mesh, Matrix4x4.Identity, joints ); }
                else { glTfScene.AddRigidMesh( mesh, Matrix4x4.Identity ); }
            }

            return Task.CompletedTask;
        }

        private static readonly object ModelLoadLock = new();

        private bool TryGetModel( Node node, ushort? deform, out string path, out Model? model ) {
            lock( ModelLoadLock ) {
                path = node.FullPath;
                if( TryLoadModel( node.FullPath, out model ) ) { return true; }

                if( TryLoadModel( node.GamePath, out model ) ) { return true; }

                if( TryLoadRacialModel( node.GamePath, deform, out var newPath, out model ) ) { return true; }

                _log.Warning( $"Could not load model\n{node.FullPath}\n{node.GamePath}\n{newPath}" );
                return false;

                bool TryLoadRacialModel( string path, ushort? cDeform, out string nPath, out Model? model ) {
                    nPath = path;
                    model = null;
                    if( cDeform == null ) { return false; }

                    nPath = Regex.Replace( path, @"c\d+", $"c{cDeform}" );
                    try {
                        model = _luminaManager.GetModel( nPath );
                        return true;
                    }
                    catch { return false; }
                }

                bool TryLoadModel( string path, out Model? model ) {
                    model = null;
                    try {
                        model = _luminaManager.GetModel( path );
                        return true;
                    }
                    catch( Exception e ) {
                        _log.Warning( e, $"Failed to load model {path}" );
                        return false;
                    }
                }
            }
        }

        private MaterialBuilder? ComposeTextures( Material xivMaterial, string outputDir, Node[]? nodes, CancellationToken cancellationToken ) {
            var xivTextureMap = new Dictionary< TextureUsage, Bitmap >();

            foreach( var xivTexture in xivMaterial.Textures ) {
                // Check for cancellation request
                if( cancellationToken.IsCancellationRequested ) { return null; }

                if( xivTexture.TexturePath == "dummy.tex" ) { continue; }

                var texturePath = xivTexture.TexturePath;
                // try find matching node for tex file
                if( nodes != null ) {
                    var nodeMatch = nodes.FirstOrDefault( x => x.GamePath == texturePath );
                    if( nodeMatch != null ) { texturePath = nodeMatch.FullPath; }
                    else {
                        var fileName = Path.GetFileNameWithoutExtension( texturePath );
                        // try get using contains
                        nodeMatch = nodes.FirstOrDefault( x => x.GamePath.Contains( fileName ) );

                        if( nodeMatch != null ) { texturePath = nodeMatch.FullPath; }
                    }
                }

                var textureBuffer = _luminaManager.GetTextureBufferCopy( texturePath, xivTexture.TexturePath );
                xivTextureMap.Add( xivTexture.TextureUsageRaw, textureBuffer );
            }

            // reference for this fuckery
            // https://docs.google.com/spreadsheets/u/0/d/1kIKvVsW3fOnVeTi9iZlBDqJo6GWVn6K6BCUIRldEjhw/htmlview#
            // genuinely not sure when to set to blend, but I think its needed for opacity on some stuff
            var alphaMode       = AlphaMode.MASK;
            var backfaceCulling = true;
            switch( xivMaterial.ShaderPack ) {
                case "character.shpk": {
                    //alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    // for character gear, split the normal map into diffuse, specular and emission
                    if( xivTextureMap.TryGetValue( TextureUsage.SamplerNormal, out var normal ) ) {
                        xivTextureMap.TryGetValue( TextureUsage.SamplerDiffuse, out var initDiffuse );
                        if( !xivTextureMap.ContainsKey( TextureUsage.SamplerDiffuse ) ||
                           !xivTextureMap.ContainsKey( TextureUsage.SamplerSpecular ) ||
                           !xivTextureMap.ContainsKey( TextureUsage.SamplerReflection ) ) {
                            var normalData = normal.LockBits( new Rectangle( 0, 0, normal.Width, normal.Height ), ImageLockMode.ReadWrite, normal.PixelFormat );
                            var initDiffuseData = initDiffuse?.LockBits( new Rectangle( 0, 0, initDiffuse.Width, initDiffuse.Height ), ImageLockMode.ReadWrite,
                                initDiffuse.PixelFormat );

                            try {
                                var characterTextures = TextureUtility.ComputeCharacterModelTextures( xivMaterial, normalData, initDiffuseData );

                                // If the textures already exist, tryAdd will make sure they are not overwritten
                                foreach( var (usage, texture) in characterTextures ) { xivTextureMap.TryAdd( usage, texture ); }
                            }
                            finally {
                                normal.UnlockBits( normalData );
                                if( initDiffuse != null && initDiffuseData != null ) { initDiffuse.UnlockBits( initDiffuseData ); }
                            }
                        }
                    }

                    if( xivTextureMap.TryGetValue( TextureUsage.SamplerMask, out var mask ) && xivTextureMap.TryGetValue( TextureUsage.SamplerSpecular, out var specularMap ) ) {
                        var maskData = mask.LockBits( new Rectangle( 0, 0, mask.Width, mask.Height ), ImageLockMode.ReadWrite, mask.PixelFormat );
                        var specularMapData = specularMap.LockBits( new Rectangle( 0, 0, specularMap.Width, specularMap.Height ), ImageLockMode.ReadWrite,
                            specularMap.PixelFormat );
                        var occlusion = TextureUtility.ComputeOcclusion( maskData, specularMapData );
                        mask.UnlockBits( maskData );
                        specularMap.UnlockBits( specularMapData );

                        // Add the specular occlusion texture to xivTextureMap
                        xivTextureMap.Add( TextureUsage.SamplerWaveMap, occlusion );
                    }

                    break;
                }
                case "skin.shpk": {
                    alphaMode = AlphaMode.MASK;
                    if( xivTextureMap.TryGetValue( TextureUsage.SamplerNormal, out var normal ) ) {
                        xivTextureMap.TryGetValue( TextureUsage.SamplerDiffuse, out var diffuse );

                        if( diffuse == null ) throw new Exception( "Diffuse texture is null" );

                        var normalData  = normal.LockBits( new Rectangle( 0, 0, normal.Width, normal.Height ), ImageLockMode.ReadWrite, normal.PixelFormat );
                        var diffuseData = diffuse.LockBits( new Rectangle( 0, 0, diffuse.Width, diffuse.Height ), ImageLockMode.ReadWrite, diffuse.PixelFormat );
                        try {
                            // use blue for opacity
                            TextureUtility.CopyNormalBlueChannelToDiffuseAlphaChannel( normalData, diffuseData );

                            for( var x = 0; x < normal.Width; x++ ) {
                                for( var y = 0; y < normal.Height; y++ ) {
                                    var normalPixel = TextureUtility.GetPixel( normalData, x, y, _log );
                                    TextureUtility.SetPixel( normalData, x, y, Color.FromArgb( normalPixel.B, normalPixel.R, normalPixel.G, 255 ) );
                                }
                            }
                        }
                        finally {
                            normal.UnlockBits( normalData );
                            diffuse.UnlockBits( diffuseData );
                        }
                    }

                    break;
                }
                case "hair.shpk": {
                    alphaMode       = AlphaMode.MASK;
                    backfaceCulling = false;
                    if( xivTextureMap.TryGetValue( TextureUsage.SamplerNormal, out var normal ) ) {
                        var specular     = new Bitmap( normal.Width, normal.Height, PixelFormat.Format32bppArgb );
                        var colorSetInfo = xivMaterial.File!.ColorSetInfo;

                        var normalData = normal.LockBits( new Rectangle( 0, 0, normal.Width, normal.Height ), ImageLockMode.ReadWrite, normal.PixelFormat );
                        try {
                            for( int x = 0; x < normalData.Width; x++ ) {
                                for( int y = 0; y < normalData.Height; y++ ) {
                                    var normalPixel     = TextureUtility.GetPixel( normalData, x, y, _log );
                                    var colorSetIndex1  = normalPixel.A / 17 * 16;
                                    var colorSetBlend   = normalPixel.A % 17 / 17.0;
                                    var colorSetIndexT2 = normalPixel.A / 17;
                                    var colorSetIndex2  = ( colorSetIndexT2 >= 15 ? 15 : colorSetIndexT2 + 1 ) * 16;

                                    var specularBlendColour = ColorUtility.BlendColorSet( in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend,
                                        ColorUtility.TextureType.Specular );

                                    // Use normal blue channel for opacity
                                    specular.SetPixel( x, y, Color.FromArgb(
                                        normalPixel.A,
                                        specularBlendColour.R,
                                        specularBlendColour.G,
                                        specularBlendColour.B
                                    ) );
                                }
                            }
                        }
                        finally { normal.UnlockBits( normalData ); }

                        xivTextureMap.Add( TextureUsage.SamplerSpecular, specular );

                        if( xivTextureMap.TryGetValue( TextureUsage.SamplerMask, out var mask ) &&
                           xivTextureMap.TryGetValue( TextureUsage.SamplerSpecular, out var specularMap ) ) {
                            // TODO: Diffuse is to be generated using character options for colors
                            // Currently based on the mask it seems I am blending it in a weird way
                            var diffuse        = ( Bitmap )mask.Clone();
                            var specularScaleX = specularMap.Width / ( float )diffuse.Width;
                            var specularScaleY = specularMap.Height / ( float )diffuse.Height;

                            var normalScaleX = normal.Width / ( float )diffuse.Width;
                            var normalScaleY = normal.Height / ( float )diffuse.Height;

                            var maskData = mask.LockBits( new Rectangle( 0, 0, mask.Width, mask.Height ), ImageLockMode.ReadWrite, mask.PixelFormat );
                            var specularMapData = specularMap.LockBits( new Rectangle( 0, 0, specularMap.Width, specularMap.Height ), ImageLockMode.ReadWrite,
                                specularMap.PixelFormat );
                            var diffuseData = diffuse.LockBits( new Rectangle( 0, 0, diffuse.Width, diffuse.Height ), ImageLockMode.ReadWrite, diffuse.PixelFormat );
                            normalData = normal.LockBits( new Rectangle( 0, 0, normal.Width, normal.Height ), ImageLockMode.ReadWrite, normal.PixelFormat );
                            try {
                                for( var x = 0; x < maskData.Width; x++ ) {
                                    for( var y = 0; y < maskData.Height; y++ ) {
                                        var maskPixel     = TextureUtility.GetPixel( maskData, x, y, _log );
                                        var specularPixel = TextureUtility.GetPixel( specularMapData, ( int )( x * specularScaleX ), ( int )( y * specularScaleY ), _log );
                                        var normalPixel   = TextureUtility.GetPixel( normalData, ( int )( x * normalScaleX ), ( int )( y * normalScaleY ), _log );

                                        TextureUtility.SetPixel( maskData, x, y, Color.FromArgb(
                                            normalPixel.A,
                                            Convert.ToInt32( specularPixel.R * Math.Pow( maskPixel.G / 255.0, 2 ) ),
                                            Convert.ToInt32( specularPixel.G * Math.Pow( maskPixel.G / 255.0, 2 ) ),
                                            Convert.ToInt32( specularPixel.B * Math.Pow( maskPixel.G / 255.0, 2 ) )
                                        ) );

                                        // Copy alpha channel from normal to diffuse
                                        // var diffusePixel = TextureUtility.GetPixel( diffuseData, x, y, _log );

                                        // TODO: Blending using mask
                                        TextureUtility.SetPixel( diffuseData, x, y, Color.FromArgb(
                                            normalPixel.A,
                                            255,
                                            255,
                                            255
                                        ) );
                                    }
                                }

                                diffuse.UnlockBits( diffuseData );
                                // Add the specular occlusion texture to xivTextureMap
                                xivTextureMap.Add( TextureUsage.SamplerDiffuse, diffuse );
                            }
                            finally {
                                mask.UnlockBits( maskData );
                                specularMap.UnlockBits( specularMapData );
                                normal.UnlockBits( normalData );
                            }
                        }
                    }

                    break;
                }
                case "iris.shpk": {
                    if( xivTextureMap.TryGetValue( TextureUsage.SamplerNormal, out var normal ) ) {
                        var specular     = new Bitmap( normal.Width, normal.Height, PixelFormat.Format32bppArgb );
                        var colorSetInfo = xivMaterial.File!.ColorSetInfo;

                        var normalData = normal.LockBits( new Rectangle( 0, 0, normal.Width, normal.Height ), ImageLockMode.ReadWrite, normal.PixelFormat );
                        try {
                            for( int x = 0; x < normal.Width; x++ ) {
                                for( int y = 0; y < normal.Height; y++ ) {
                                    //var normalPixel = normal.GetPixel( x, y );
                                    var normalPixel     = TextureUtility.GetPixel( normalData, x, y, _log );
                                    var colorSetIndex1  = normalPixel.A / 17 * 16;
                                    var colorSetBlend   = normalPixel.A % 17 / 17.0;
                                    var colorSetIndexT2 = normalPixel.A / 17;
                                    var colorSetIndex2  = ( colorSetIndexT2 >= 15 ? 15 : colorSetIndexT2 + 1 ) * 16;

                                    var specularBlendColour = ColorUtility.BlendColorSet( in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend,
                                        ColorUtility.TextureType.Specular );

                                    specular.SetPixel( x, y, Color.FromArgb( 255, specularBlendColour.R, specularBlendColour.G, specularBlendColour.B ) );
                                }
                            }

                            xivTextureMap.Add( TextureUsage.SamplerSpecular, specular );
                        }
                        finally { normal.UnlockBits( normalData ); }
                    }

                    break;
                }
                default:
                    _log.Debug( $"Unhandled shader pack {xivMaterial.ShaderPack}" );
                    break;
            }

            var glTfMaterial = new MaterialBuilder {
                Name        = xivMaterial.File?.FilePath.Path,
                AlphaMode   = alphaMode,
                DoubleSided = !backfaceCulling
            };

            ExportTextures( glTfMaterial, xivTextureMap, outputDir );

            return glTfMaterial;
        }


        private void ExportTextures( MaterialBuilder glTfMaterial, Dictionary< TextureUsage, Bitmap > xivTextureMap, string outputDir ) {
            foreach( var xivTexture in xivTextureMap ) { ExportTexture( glTfMaterial, xivTexture.Key, xivTexture.Value, outputDir ); }

            // Set the metallic roughness factor to 0
            glTfMaterial.WithMetallicRoughness( 0 );
        }

        private async void ExportTexture( MaterialBuilder glTfMaterial, TextureUsage textureUsage, Bitmap bitmap, string outputDir ) {
            await TextureSemaphore.WaitAsync();
            try {
                // tbh can overwrite or delete these after use but theyre helpful for debugging
                var    name = glTfMaterial.Name.Replace( "\\", "/" ).Split( "/" ).Last().Split( "." ).First();
                string path;

                // Save the texture to the output directory and update the glTF material with respective image paths
                switch( textureUsage ) {
                    case TextureUsage.SamplerColorMap0:
                    case TextureUsage.SamplerDiffuse:
                        path = Path.Combine( outputDir, $"{name}_diffuse.png" );
                        bitmap.Save( path );
                        glTfMaterial.WithBaseColor( path );
                        break;
                    case TextureUsage.SamplerNormalMap0:
                    case TextureUsage.SamplerNormal:
                        path = Path.Combine( outputDir, $"{name}_normal.png" );
                        bitmap.Save( path );
                        glTfMaterial.WithNormal( path );
                        break;
                    case TextureUsage.SamplerSpecularMap0:
                    case TextureUsage.SamplerSpecular:
                        path = Path.Combine( outputDir, $"{name}_specular.png" );
                        bitmap.Save( path );
                        glTfMaterial.WithSpecularColor( path );
                        break;
                    case TextureUsage.SamplerWaveMap:
                        path = Path.Combine( outputDir, $"{name}_occlusion.png" );
                        bitmap.Save( path );
                        glTfMaterial.WithOcclusion( path );
                        break;
                    case TextureUsage.SamplerReflection:
                        path = Path.Combine( outputDir, $"{name}_emissive.png" );
                        bitmap.Save( path );
                        glTfMaterial.WithEmissive( path, new Vector3( 255, 255, 255 ) );
                        break;
                    case TextureUsage.SamplerMask:
                        path = Path.Combine( outputDir, $"{name}_mask.png" );
                        // Do something with this texture
                        bitmap.Save( path );
                        break;
                    default:
                        _log.Warning( "Unhandled TextureUsage: " + textureUsage );
                        path = Path.Combine( outputDir, $"{name}_{textureUsage}.png" );
                        bitmap.Save( path );
                        break;
                }
            }
            finally { TextureSemaphore.Release(); }
        }

        /// <summary>
        /// Compute the distance between two strings.
        /// Using for matching default materials to ones which may be race-specific reported by penumbra.
        /// </summary>
        private static int ComputeLd( string s, string t ) {
            var n = s.Length;
            var m = t.Length;
            var d = new int[n + 1, m + 1];

            // Step 1
            if( n == 0 ) { return m; }

            if( m == 0 ) { return n; }

            // Step 2
            for( var i = 0; i <= n; d[ i, 0 ] = i++ ) { }

            for( var j = 0; j <= m; d[ 0, j ] = j++ ) { }

            // Step 3
            for( var i = 1; i <= n; i++ ) {
                //Step 4
                for( var j = 1; j <= m; j++ ) {
                    // Step 5
                    var cost = t[ j - 1 ] == s[ i - 1 ] ? 0 : 1;

                    // Step 6
                    d[ i, j ] = Math.Min(
                        Math.Min( d[ i - 1, j ] + 1, d[ i, j - 1 ] + 1 ),
                        d[ i - 1, j - 1 ] + cost );
                }
            }

            // Step 7
            return d[ n, m ];
        }
    }
}