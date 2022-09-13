using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Meshes;
using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace FModel.Views.Snooper;

public class Snooper
{
    private readonly IWindow _window;
    private GL _gl;
    private SnimGui _imGui;
    private Camera _camera;
    private IKeyboard _keyboard;
    private IMouse _mouse;
    private RawImage _icon;

    private readonly FramebufferObject _framebuffer;
    private readonly Skybox _skybox;
    private readonly Grid _grid;

    private Shader _shader;
    private Shader _outline;
    private Vector3 _diffuseLight;
    private Vector3 _specularLight;
    private readonly Dictionary<FGuid, Model> _models;

    private Vector2D<int> _size;
    private float _previousSpeed;
    private bool _append;

    public Snooper()
    {
        const double ratio = .7;
        var x = SystemParameters.MaximizedPrimaryScreenWidth;
        var y = SystemParameters.MaximizedPrimaryScreenHeight;

        var options = WindowOptions.Default;
        options.Size = _size = new Vector2D<int>(Convert.ToInt32(x * ratio), Convert.ToInt32(y * ratio));
        options.WindowBorder = WindowBorder.Fixed;
        options.Title = "Snooper";
        _window = Silk.NET.Windowing.Window.Create(options);

        unsafe
        {
            var info = Application.GetResourceStream(new Uri("/FModel;component/Resources/materialicon.png", UriKind.Relative));
            using var image = Image.Load<Rgba32>(info.Stream);
            var memoryGroup = image.GetPixelMemoryGroup();
            Memory<byte> array = new byte[memoryGroup.TotalLength * sizeof(Rgba32)];
            var block = MemoryMarshal.Cast<byte, Rgba32>(array.Span);
            foreach (var memory in memoryGroup)
            {
                memory.Span.CopyTo(block);
                block = block.Slice(memory.Length);
            }
            _icon = new RawImage(image.Width, image.Height, array);
        }

        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClose;
        _window.FramebufferResize += delegate(Vector2D<int> vector2D)
        {
            _gl.Viewport(vector2D);
            _size = vector2D;
        };

        _framebuffer = new FramebufferObject(_size);
        _skybox = new Skybox();
        _grid = new Grid();
        _models = new Dictionary<FGuid, Model>();
    }

    public void Run(UObject export)
    {
        switch (export)
        {
            case UStaticMesh st when st.TryConvert(out var mesh):
            {
                var guid = st.LightingGuid;
                if (!_models.TryGetValue(guid, out _))
                {
                    _models[guid] = new Model(export, st.Name, st.ExportType, mesh.LODs[0], mesh.LODs[0].Verts);
                    SetupCamera(mesh.BoundingBox *= Constants.SCALE_DOWN_RATIO);
                }
                break;
            }
            case USkeletalMesh sk when sk.TryConvert(out var mesh):
            {
                var guid = Guid.NewGuid();
                if (!_models.TryGetValue(guid, out _))
                {
                    _models[guid] = new Model(export, sk.Name, sk.ExportType, mesh.LODs[0], mesh.LODs[0].Verts, sk.MorphTargets, mesh.RefSkeleton);
                    SetupCamera(mesh.BoundingBox *= Constants.SCALE_DOWN_RATIO);
                }
                break;
            }
            case UMaterialInstance mi:
            {
                var guid = Guid.NewGuid();
                if (!_models.TryGetValue(guid, out _))
                {
                    _models[guid] = new Cube(export, mi.Name, mi.ExportType, mi);
                    SetupCamera(new FBox(new FVector(-.65f), new FVector(.65f)));
                }
                break;
            }
            case UWorld wd:
            {
                var persistentLevel = wd.PersistentLevel.Load<ULevel>();
                for (var i = 0; i < persistentLevel.Actors.Length; i++)
                {
                    if (persistentLevel.Actors[i].Load() is not { } actor || actor.ExportType == "LODActor" ||
                        !actor.TryGetValue(out FPackageIndex staticMeshComponent, "StaticMeshComponent") ||
                        staticMeshComponent.Load() is not { } staticMeshComp) continue;

                    if (!staticMeshComp.TryGetValue(out FPackageIndex staticMesh, "StaticMesh") && actor.Class is UBlueprintGeneratedClass)
                    {
                        foreach (var actorExp in actor.Class.Owner.GetExports())
                            if (actorExp.TryGetValue(out staticMesh, "StaticMesh"))
                                break;
                    }

                    if (staticMesh?.Load() is not UStaticMesh m || !m.TryConvert(out var mesh))
                        continue;

                    var guid = m.LightingGuid;
                    var transform = new Transform
                    {
                        Position = staticMeshComp.GetOrDefault("RelativeLocation", FVector.ZeroVector) * Constants.SCALE_DOWN_RATIO,
                        Rotation = staticMeshComp.GetOrDefault("RelativeRotation", FRotator.ZeroRotator),
                        Scale = staticMeshComp.GetOrDefault("RelativeScale3D", FVector.OneVector)
                    };
                    // can't seem to find the problem here
                    // some meshes should have their yaw reversed and others not
                    transform.Rotation.Yaw = -transform.Rotation.Yaw;
                    if (_models.TryGetValue(guid, out var model))
                    {
                        model.AddInstance(transform);
                        continue;
                    }

                    model = new Model(export, m.Name, m.ExportType, mesh.LODs[0], mesh.LODs[0].Verts, null, null, transform);
                    if (actor.TryGetAllValues(out FPackageIndex[] textureData, "TextureData"))
                    {
                        for (int j = 0; j < textureData.Length; j++)
                        {
                            if (textureData[j].Load() is not { } textureDataIdx)
                                continue;

                            if (textureDataIdx.TryGetValue(out FPackageIndex diffuse, "Diffuse") &&
                                diffuse.Load() is UTexture2D diffuseTexture)
                                model.Sections[j].Parameters.Diffuse = diffuseTexture;
                            if (textureDataIdx.TryGetValue(out FPackageIndex normal, "Normal") &&
                                normal.Load() is UTexture2D normalTexture)
                                model.Sections[j].Parameters.Normal = normalTexture;
                            if (textureDataIdx.TryGetValue(out FPackageIndex specular, "Specular") &&
                                specular.Load() is UTexture2D specularTexture)
                                model.Sections[j].Parameters.Specular = specularTexture;
                        }
                    }
                    if (staticMeshComp.TryGetValue(out FPackageIndex[] overrideMaterials, "OverrideMaterials"))
                    {
                        var max = model.Sections.Length - 1;
                        for (var j = 0; j < overrideMaterials.Length; j++)
                        {
                            if (j > max) break;
                            if (overrideMaterials[j].Load() is not UMaterialInterface unrealMaterial) continue;
                            model.Sections[j].SwapMaterial(unrealMaterial);
                        }
                    }

                    _models[guid] = model;
                }
                _camera = new Camera(new Vector3(0f, 5f, 5f), Vector3.Zero, 0.01f, 1000f, 5f);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(export));
        }

        DoLoop();
    }

    private void DoLoop()
    {
        if (_append) _append = false;
        _window.Run();
        // if (_window.IsInitialized)
        // {
        //     if (!_window.GLContext.IsCurrent)
        //     {
        //         _window.GLContext.MakeCurrent();
        //     }
        //
        //     _append = false;
        //     _window.IsVisible = true;
        //     var model = _models.Last();
        //     model.Value.Setup(_gl);
        //     _imGui.Increment(model.Key);
        // }
        // else _window.Initialize();
        //
        // while (!_window.IsClosing && _window.IsVisible)
        // {
        //     _window.DoEvents();
        //     if (!_window.IsClosing && _window.IsVisible)
        //         _window.DoUpdate();
        //     if (_window.IsClosing || !_window.IsVisible)
        //         return;
        //     _window.DoRender();
        // }
        //
        // _window.DoEvents();
        // if (_window.IsClosing) _window.Reset();
    }

    private void SetupCamera(FBox box)
    {
        var far = box.Max.Max();
        var center = box.GetCenter();
        var position = new Vector3(0f, center.Z, box.Max.Y * 3);
        var speed = far / 2f;
        if (speed > _previousSpeed)
        {
            _camera = new Camera(position, center, 0.01f, far * 50f, speed);
            _previousSpeed = _camera.Speed;
        }
    }

    private void OnLoad()
    {
        _window.SetWindowIcon(ref _icon);
        _window.Center();

        var input = _window.CreateInput();
        _keyboard = input.Keyboards[0];
        _mouse = input.Mice[0];

        _gl = GL.GetApi(_window);
        _gl.Enable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Multisample);
        _gl.StencilOp(StencilOp.Keep, StencilOp.Replace, StencilOp.Replace);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _imGui = new SnimGui(_gl, _window, input);

        _framebuffer.Setup(_gl);
        _skybox.Setup(_gl);
        _grid.Setup(_gl);

        _shader = new Shader(_gl);
        _outline = new Shader(_gl, "outline");
        _diffuseLight = new Vector3(0.75f);
        _specularLight = new Vector3(0.5f);
        foreach (var model in _models.Values)
        {
            model.Setup(_gl);
        }
    }

    /// <summary>
    /// friendly reminder this is called each frame
    /// don't do crazy things inside
    /// </summary>
    private void OnRender(double deltaTime)
    {
        _imGui.Update((float) deltaTime);

        ClearWhatHasBeenDrawn(); // in main window

        _framebuffer.Bind(); // switch to dedicated window
        ClearWhatHasBeenDrawn(); // in dedicated window

        _skybox.Bind(_camera);
        _grid.Bind(_camera);

        var viewMatrix = _camera.GetViewMatrix();
        var projMatrix = _camera.GetProjectionMatrix();

        _outline.Use();
        _outline.SetUniform("uView", viewMatrix);
        _outline.SetUniform("uProjection", projMatrix);
        _outline.SetUniform("viewPos", _camera.Position);

        _shader.Use();
        _shader.SetUniform("uView", viewMatrix);
        _shader.SetUniform("uProjection", projMatrix);
        _shader.SetUniform("viewPos", _camera.Position);

        _shader.SetUniform("material.diffuseMap", 0);
        _shader.SetUniform("material.normalMap", 1);
        _shader.SetUniform("material.specularMap", 2);
        _shader.SetUniform("material.emissionMap", 3);

        _shader.SetUniform("light.position", _camera.Position);
        _shader.SetUniform("light.diffuse", _diffuseLight);
        _shader.SetUniform("light.specular", _specularLight);

        foreach (var model in _models.Values.Where(model => model.Show))
        {
            model.Bind(_shader);
        }
        _gl.Enable(EnableCap.StencilTest); // I don't get why this must be here but it works now so...
        foreach (var model in _models.Values.Where(model => model.IsSelected && model.Show))
        {
            model.Outline(_outline);
        }

        _imGui.Construct(_size, _framebuffer, _camera, _mouse, _models);

        _framebuffer.BindMsaa();
        _framebuffer.Bind(0); // switch back to main window
        _framebuffer.BindStuff();

        _imGui.Render(); // render ImGui in main window
    }

    private void ClearWhatHasBeenDrawn()
    {
        _gl.ClearColor(1.0f, 0.102f, 0.129f, 1.0f);
        _gl.Clear((uint) ClearBufferMask.ColorBufferBit | (uint) ClearBufferMask.DepthBufferBit | (uint) ClearBufferMask.StencilBufferBit);
        _gl.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    private void OnUpdate(double deltaTime)
    {
        if (ImGui.GetIO().WantTextInput) return;
        var multiplier = _keyboard.IsKeyPressed(Key.ShiftLeft) ? 2f : 1f;
        var moveSpeed = _camera.Speed * multiplier * (float) deltaTime;
        if (_keyboard.IsKeyPressed(Key.W))
            _camera.Position += moveSpeed * _camera.Direction;
        if (_keyboard.IsKeyPressed(Key.S))
            _camera.Position -= moveSpeed * _camera.Direction;
        if (_keyboard.IsKeyPressed(Key.A))
            _camera.Position -= Vector3.Normalize(Vector3.Cross(_camera.Direction, _camera.Up)) * moveSpeed;
        if (_keyboard.IsKeyPressed(Key.D))
            _camera.Position += Vector3.Normalize(Vector3.Cross(_camera.Direction, _camera.Up)) * moveSpeed;
        if (_keyboard.IsKeyPressed(Key.E))
            _camera.Position += moveSpeed * _camera.Up;
        if (_keyboard.IsKeyPressed(Key.Q))
            _camera.Position -= moveSpeed * _camera.Up;

        if (_keyboard.IsKeyPressed(Key.H))
        {
            // because we lose GLContext when the window is invisible after a few seconds (it's apparently a bug)
            // we can't use GLContext back on next load and so, for now, we basically have to reset the window
            // if we can't use GLContext, we can't generate handles, can't interact with IsVisible, State, etc
            // tldr we dispose everything but don't clear models, so the more you append, the longer it takes to load
            _append = true;
            _window.Close();
            // _window.IsVisible = false;
        }
        if (_keyboard.IsKeyPressed(Key.Escape))
            _window.Close();
    }

    private void OnClose()
    {
        _framebuffer.Dispose();
        _grid.Dispose();
        _skybox.Dispose();
        _shader.Dispose();
        _outline.Dispose();
        foreach (var model in _models.Values)
        {
            model.Dispose();
        }
        if (!_append)
        {
            _models.Clear();
            _previousSpeed = 0f;
        }
        _imGui.Dispose();
        _window.Dispose();
        _gl.Dispose();
    }
}
