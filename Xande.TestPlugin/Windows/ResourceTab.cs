using ImGuiNET;
using Xande.Havok;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Penumbra.Api;
using Newtonsoft.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface;
using Xande.TestPlugin.Models;

namespace Xande.TestPlugin.Windows {
    public class ResourceTab : IDisposable {
        private readonly IPluginLog                                                                                       _log;
        private readonly OnScreenExporter                                                                                 _onScreenExporter;
        private readonly DalamudPluginInterface                                                                           _dalamudPluginInterface;
        private          Task<(string name, string hash, ResourceTree tree, DateTime refreshedAt, bool[] exportOptions)>? _resourceTask;
        private          (string name, string hash, ResourceTree tree, DateTime refreshedAt, bool[] exportOptions)?       _resourceTaskResult;
        private          CancellationTokenSource                                                                          _exportCts;
        private readonly FileDialogManager                                                                                _manager;
        private          Task?                                                                                            _exportTask;
        private          int                                                                                              _selectedGameObjectIndex;
        private          string                                                                                           _searchFilter  = string.Empty;
        private          bool                                                                                             _autoExport    = true;
        private readonly string                                                                                           _tempDirectory = Path.Combine( Path.GetTempPath(), "Penumbra.XandeTest" );


        public ResourceTab( LuminaManager luminaManager, HavokConverter converter ) {
            _dalamudPluginInterface = Service.PluginInterface;
            _log = Service.Logger;
            _exportCts = new CancellationTokenSource();
            _manager = new FileDialogManager {
                AddedWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
            };
            _manager.CustomSideBarItems.Add( ("Penumbra.XandeTest", _tempDirectory, FontAwesomeIcon.Folder, 0) );
            _onScreenExporter = new OnScreenExporter( converter, luminaManager );
        }

        public void Dispose() {
            _exportCts.Dispose();
            _exportTask?.Dispose();
        }

        private void DrawResourceListView() {
            _manager.Draw();

            // show load from disk button
            if( ImGui.Button( "Load from disk" ) ) {
                _manager.OpenFileDialog( "Select Resource File", "Json Files{.json}", ( selected, paths ) => {
                    if( !selected ) return;
                    if (paths.Count == 0) {
                        return;
                    }
                    var path = paths[0];
                    _resourceTask = LoadResourceListFromDisk( path );
                }, 1, startPath: _tempDirectory, isModal: true );
            }

            var objects = Service.ObjectTable.Where( x => x.IsValid() )
                .Where( x =>
                    x.ObjectKind is ObjectKind.Player or ObjectKind.BattleNpc or ObjectKind.Retainer or ObjectKind.EventNpc or ObjectKind.Companion
                ).ToArray();
            if( objects.Length == 0 ) {
                ImGui.Text( "No game objects found" );
            }
            else {
                // combo for selecting game object
                ImGui.Text( "Select Game Object" );
                var selected = _selectedGameObjectIndex;

                // text input to allow searching
                var filter = _searchFilter;
                if( ImGui.InputText( "##Filter", ref filter, 100 ) ) {
                    _searchFilter = filter;
                }
                if( !string.IsNullOrEmpty( filter ) ) {
                    objects = objects.Where( x => x.Name.ToString().Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToArray();
                    // edit index if it's out of range
                    if( selected >= objects.Length ) {
                        selected = objects.Length - 1;
                    }
                }

                var names = objects.Select( x => $"{x.Name} - {x.ObjectKind}" ).ToArray();

                if( ImGui.Combo( "##GameObject", ref selected, names, names.Length ) ) {
                    _selectedGameObjectIndex = selected;
                    _resourceTask = RefreshResourceList( objects[selected].ObjectIndex, _resourceTask?.Result );
                }
                else {
                    // auto refresh while logged in
                    if( Service.ClientState.IsLoggedIn &&
                       (_resourceTask == null || _resourceTask.IsCompleted) &&
                       (_resourceTask == null || _resourceTask.Result.refreshedAt.AddSeconds( 0.1 ) < DateTime.UtcNow) ) {
                        _resourceTask = RefreshResourceList( objects[ selected ].ObjectIndex, _resourceTask?.Result );
                    }
                }
            }

            if (_resourceTask == null) {
                ImGui.Text( "No resources found" );
                return;
            }

            if( _resourceTaskResult != null ) {
                // check if hash is different
                if( _resourceTask != null && _resourceTask.IsCompletedSuccessfully && _resourceTask.Result.hash != _resourceTaskResult.Value.hash ) {
                    _resourceTaskResult = _resourceTask.Result;
                }
            }
            else if( _resourceTask != null ) {
                    if( !_resourceTask.IsCompleted ) { ImGui.Text( "Loading..." ); }
                    else if( _resourceTask.Exception != null ) { ImGui.Text( $"Error loading resources\n\n{_resourceTask.Exception}" ); }
                    else if( _resourceTask.IsCompletedSuccessfully ) { _resourceTaskResult = _resourceTask.Result; }
            }

            // dropdown to select
            if( !_resourceTaskResult.HasValue ) {
                ImGui.Text( "No resources found" );
                return;
            }

            using var child = ImRaii.Child( "##Data" );
            if( !child )
                return;

            var resourceTaskResult = _resourceTaskResult.Value;

            ImGui.Text( resourceTaskResult.name );
            DrawResourceTree( resourceTaskResult.tree, ref resourceTaskResult.exportOptions );
        }
        private void DrawResourceTree( ResourceTree resourceTree, ref bool[] exportOptions ) {

            // disable buttons if exporting
            bool disableExport = _exportTask != null;
            if( disableExport ) {
                ImGui.PushStyleVar( ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f );
            }

            // export button
            if( ImGui.Button( $"Export {exportOptions.Count(x => x)} selected" ) && _exportTask == null ) {
                _exportTask = _onScreenExporter.ExportResourceTree( resourceTree, exportOptions, true, _exportCts.Token );
            }
            ImGui.SameLine();
            // export all button
            if( ImGui.Button( "Export All" ) && _exportTask == null ) {
                _exportTask = _onScreenExporter.ExportResourceTree( resourceTree, new bool[resourceTree.Nodes.Length].Select( _ => true ).ToArray(), true, _exportCts.Token );
            }

            if( disableExport ) {
                ImGui.PopStyleVar();
            }

            // auto export checkbox
            ImGui.SameLine();
            ImGui.Checkbox( "Auto Export", ref _autoExport );
            // hover to show tooltip
            if( ImGui.IsItemHovered() ) {
                ImGui.SetTooltip( "Automatically export when resources are changed" );
            }

            // cancel button
            if( _exportTask == null ) {
                ImGui.SameLine();
                ImGui.Text( "No export in progress" );
            }
            else {
                // show cancelling... if cancelled but not completed.
                // if completed then show export again
                if( _exportTask.IsCompleted ) {
                    _exportTask = null!;
                }
                else if( _exportTask.IsCanceled ) {
                    ImGui.SameLine();
                    ImGui.TextUnformatted( "Export Cancelled..." );
                }
                else {
                    ImGui.SameLine();
                    if( ImGui.Button( "Cancel Export" ) ) {
                        _exportCts.Cancel();
                        _exportCts.Dispose();
                        _exportCts = new CancellationTokenSource();
                    }
                }
            }

            using var table = ImRaii.Table( "##ResourceTable", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable );
            if( !table )
                return;

            ImGui.TableSetupColumn( string.Empty, ImGuiTableColumnFlags.WidthFixed, 250 );
            ImGui.TableSetupColumn( "GamePath", ImGuiTableColumnFlags.WidthStretch, 0.3f );
            ImGui.TableSetupColumn( "FullPath", ImGuiTableColumnFlags.WidthStretch, 0.5f );
            ImGui.TableHeadersRow();

            for( int i = 0; i < resourceTree.Nodes.Length; i++ ) {
                var node = resourceTree.Nodes[i];
                var exportOption = exportOptions[i];

                // only interested in mdl, sklb and tex
                var type = ( Penumbra.Api.Enums.ResourceType )node.Type;
                if( type != Penumbra.Api.Enums.ResourceType.Mdl
                    && type != Penumbra.Api.Enums.ResourceType.Sklb
                    && type != Penumbra.Api.Enums.ResourceType.Tex )
                    continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if( node.Children.Length > 0 ) {
                    if( type == Penumbra.Api.Enums.ResourceType.Mdl ) {
                        ImGui.Checkbox( $"##{node.GetHashCode()}", ref exportOption );
                        exportOptions[i] = exportOption;
                        // hover to show tooltip
                        if( ImGui.IsItemHovered() ) {
                            ImGui.SetTooltip( $"Export \"{node.DisplayName}\"" );
                        }

                        // quick export button
                        ImGui.SameLine();
                        // imgui button download icon


                        ImGui.PushFont( UiBuilder.IconFont );
                        if( ImGui.Button( $"{FontAwesomeIcon.FileExport.ToIconString()}##{node.GetHashCode()}" ) && _exportTask == null ) {
                            var tmpExportOptions = new bool[resourceTree.Nodes.Length];
                            tmpExportOptions[i] = true;
                            _exportTask = _onScreenExporter.ExportResourceTree( resourceTree, tmpExportOptions, true, _exportCts.Token );
                        }
                        ImGui.PopFont();
                        if ( ImGui.IsItemHovered() ) {
                            ImGui.SetTooltip( $"Export \"{node.DisplayName}\" as individual model" );
                        }

                        ImGui.SameLine();
                    }

                    using var section = ImRaii.TreeNode( $"{node.DisplayName}##{node.GetHashCode()}", ImGuiTreeNodeFlags.SpanAvailWidth );

                    // only render current row
                    ImGui.TableNextColumn();
                    DrawCopyableText( node.GamePath );
                    ImGui.TableNextColumn();
                    DrawCopyableText( node.FullPath );

                    if( !section ) continue;
                    DrawResourceNode( node );
                    // add line to separate
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    // vertical spacing to help separate next node
                    ImGui.Dummy( new Vector2( 0, 10 ) );
                }
                else {
                    using var section = ImRaii.TreeNode( $"{node.DisplayName}##{node.GetHashCode()}", ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.Leaf );
                    ImGui.TableNextColumn();
                    DrawCopyableText( node.GamePath );
                    ImGui.TableNextColumn();
                    DrawCopyableText( node.FullPath );
                }
            }
        }

        private void DrawCopyableText( string text ) {
            ImGui.Text( text );
            // click to copy
            if( ImGui.IsItemClicked( ImGuiMouseButton.Right ) ) {
                ImGui.SetClipboardText( text );
            }
            // hover to show tooltip
            if( ImGui.IsItemHovered() ) {
                ImGui.SetTooltip( $"Copy \"{text}\" to clipboard" );
            }
        }

        private void DrawResourceNode( Node node ) {
            // add same data to the table, expandable if more children, increase indent in first column
            // indent
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if( node.Children.Length > 0 ) {
                ImGui.Dummy( new Vector2( 5, 0 ) );
                ImGui.SameLine();

                // default open all children
                ImGui.SetNextItemOpen( true, ImGuiCond.Once );
                using var section = ImRaii.TreeNode( $"{node.DisplayName}##{node.GetHashCode()}", ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.Bullet );
                ImGui.TableNextColumn();
                DrawCopyableText( node.GamePath );
                ImGui.TableNextColumn();
                DrawCopyableText( node.FullPath );

                if( section ) {
                    foreach( var child in node.Children ) {
                        DrawResourceNode( child );
                    }
                }
            }
            else {
                using var section = ImRaii.TreeNode( $"{node.DisplayName}##{node.GetHashCode()}", ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.Leaf );
                ImGui.TableNextColumn();
                DrawCopyableText( node.GamePath );
                ImGui.TableNextColumn();
                DrawCopyableText( node.FullPath );
            }
        }

        public void Draw() {
            DrawResourceListView();
        }

        private Task<(string, string, ResourceTree, DateTime, bool[])> LoadResourceListFromDisk(string pathToFile) {
            return Task.Run( () => {
                try {
                    if( !File.Exists( pathToFile ) ) {
                        throw new Exception( "No resource file found" );
                    }

                    var contents = File.ReadAllText( pathToFile );
                    var resourceTree = JsonConvert.DeserializeObject<ResourceTree>( contents );

                    if( resourceTree == null ) {
                        throw new Exception( "No resource trees found" );
                    }

                    if( resourceTree.Nodes.Length == 0 ) {
                        throw new Exception( "No resources found" );
                    }

                    var contentHash = Convert.ToBase64String( SHA256.HashData( Encoding.UTF8.GetBytes( contents ) ) );
                    var exportOptions = new bool[resourceTree.Nodes.Length];
                    return (resourceTree.Name, contentHash, resourceTree, DateTime.UtcNow, exportOptions);
                } catch( Exception e ) {
                    _log.Error( e, "Error loading resources from file" );
                    throw;
                }
            } );
        }

        private Task<(string, string, ResourceTree, DateTime, bool[])> RefreshResourceList( ushort gameObjectIndex,  (string, string, ResourceTree, DateTime, bool[])? previous) {
            return Task.Run( () => {
                try {
                    var ipcResult = Ipc.GetSerializedResourceTrees.Subscriber( _dalamudPluginInterface ).Invoke( true, new[] { gameObjectIndex } );
                    if( ipcResult == default ) {
                        throw new Exception( "IPC call failed" );
                    }

                    if( ipcResult.Count == 0 || ipcResult[0] == default) {
                        return ( "Empty Tree", "", new ResourceTree(), DateTime.UtcNow, Array.Empty< bool >() );
                    }

                    var treeResult = ipcResult[0];
                    var resourceTree = JsonConvert.DeserializeObject<ResourceTree>( treeResult.Item2 );
                    if( resourceTree == null ) {
                        return ( "No Tree", "", new ResourceTree(), DateTime.UtcNow, Array.Empty< bool >() );
                    }

                    if( resourceTree.Nodes.Length == 0 ) {
                        return ( "No Nodes", "", new ResourceTree(), DateTime.UtcNow, Array.Empty< bool >() );
                    }

                    // hash response for comparison later
                    var contentHash = Convert.ToBase64String( SHA256.HashData( Encoding.UTF8.GetBytes( treeResult.Item2 ) ) );
                    if ( previous != null && previous.Value.Item2 == contentHash ) {
                        // copy but update refreshedAt
                        return (treeResult.Item1, contentHash, resourceTree, DateTime.UtcNow, previous.Value.Item5);
                    }

                    // if length same, use previous export options
                    var exportOptions = new bool[resourceTree.Nodes.Length];
                    if( previous != null && previous.Value.Item5.Length == exportOptions.Length ) {
                        previous.Value.Item5.CopyTo( exportOptions, 0 );
                    }

                    // make safe name with timestamp
                    var name = $"{resourceTree.Name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
                    // filter out invalid characters
                    var resourceFile = Path.Combine( _tempDirectory, string.Concat( name.Split( Path.GetInvalidFileNameChars() ) ) );
                    Directory.CreateDirectory( _tempDirectory );
                    File.WriteAllText( resourceFile, treeResult.Item2 );

                    return (treeResult.Item1, contentHash, resourceTree, DateTime.UtcNow, exportOptions);
                }
                catch( Exception e ) {
                    _log.Error( e, "Error loading resources" );
                    throw;
                }
            } );
        }
    }
}
