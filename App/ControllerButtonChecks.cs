using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace SteamDashET;

internal static class ControllerButtonChecks
{
    internal static void Run()
    {
        using var native = new FakeControllers();
        double now = 0;
        var reader = new ControllerButtonInput(native.System, native.Input, native.Models, () => now);
        Require(native.Reads == 0, "Constructing controller polling performs no native reads");
        native.Flags[(201, "button_a")] = (uint)EVRComponentProperty.IsPressed;
        native.Flags[(202, "button_b")] = (uint)EVRComponentProperty.IsTouched;
        var first = reader.Read();
        Require(first.Left.A.Available && first.Left.A.Pressed && first.Left.B.Available && !first.Left.B.Pressed &&
            first.Right.A.Available && !first.Right.A.Pressed && first.Right.B.Available && !first.Right.B.Pressed &&
            first.Left.Identity.RenderModel == "index-left" && first.Left.Identity.SerialNumber == "left-serial" &&
            first.Left.Identity.InputSourceHandle == 201 && first.Right.Identity.InputSourceHandle == 202,
            "Model polling reads each hand and A/B independently; touch flags alone are not presses");
        int metadataReads = native.MetadataReads, enumerations = native.ComponentEnumerations;
        native.Flags[(201, "button_a")] = 0;
        var next = reader.Read();
        Require(!next.Left.A.Pressed && first.Left.A.Pressed && next.Left.Identity == first.Left.Identity &&
            native.MetadataReads == metadataReads && native.ComponentEnumerations == enumerations,
            "Button snapshots retain old samples while metadata and component discovery are cached");

        native.Flags[(201, "button_a")] = (uint)EVRComponentProperty.IsPressed;
        native.Flags[(201, "button_b")] = (uint)EVRComponentProperty.IsPressed;
        native.Failures.Add((201, "button_a"));
        var failed = reader.Read();
        Require(!failed.Left.A.Available && !failed.Left.A.Pressed && failed.Left.B.Available && failed.Left.B.Pressed,
            "Failed component reads discard stale pressed flags without disabling the other button");
        native.Failures.Clear();

        native.LeftConnected = false;
        var disconnected = reader.Read();
        Require(!disconnected.Left.A.Available && !disconnected.Left.B.Available &&
            disconnected.Left.Identity != first.Left.Identity && disconnected.Right.A.Available,
            "Disconnects invalidate the hand identity and leave the other controller available");
        native.LeftConnected = true;
        var reconnected = reader.Read();
        Require(reconnected.Left.A.Available && reconnected.Left.Identity != first.Left.Identity,
            "Reconnecting at the same tracked-device index creates a new click identity");

        native.LeftIndex = 4;
        native.RightIndex = 3;
        var swapped = reader.Read();
        Require(swapped.Left.Identity.DeviceIndex == 4 && swapped.Left.Identity.RenderModel == "index-right" &&
            swapped.Left.Identity != reconnected.Left.Identity && swapped.Right.Identity.DeviceIndex == 3,
            "Role reassignment refreshes model metadata and invalidates the previous hand identity");
        native.LeftIndex = 3;
        native.RightIndex = 4;
        reader.Read();
        native.Devices[3] = ("knuckles", "index-left", "replacement-serial");
        now = 1.01;
        var replaced = reader.Read();
        Require(replaced.Left.Identity.SerialNumber == "replacement-serial" && replaced.Left.Identity != reconnected.Left.Identity,
            "Periodic identity refresh recognizes a same-index replacement by serial number");
        var beforeEvent = replaced.Left.Identity;
        reader.Invalidate();
        Require(reader.Read().Left.Identity != beforeEvent,
            "Device events reset click identities even when the replacement reports the same model and index");
        var beforeTargeted = reader.Read();
        reader.Invalidate(3);
        var afterTargeted = reader.Read();
        Require(afterTargeted.Left.Identity != beforeTargeted.Left.Identity &&
            afterTargeted.Right.Identity == beforeTargeted.Right.Identity,
            "A controller event invalidates only that hand and preserves the other hand's click identity");
        reader.Invalidate(9);
        Require(reader.Read().Left.Identity == afterTargeted.Left.Identity &&
            reader.Read().Right.Identity == afterTargeted.Right.Identity,
            "Unrelated device events leave both controller identities intact");
        native.SerialFailure = true;
        now = 2.02;
        var missingSerial = reader.Read();
        now = 3.03;
        var stillMissingSerial = reader.Read();
        Require(missingSerial.Left.A.Available && missingSerial.Left.Identity.SerialNumber == "" &&
            stillMissingSerial.Left.Identity == missingSerial.Left.Identity &&
            stillMissingSerial.Right.Identity == missingSerial.Right.Identity,
            "An unavailable optional serial number does not reset click identities at each metadata refresh");
        native.SerialFailure = false;

        native.LeftIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
        int callsBeforeMissing = native.ComponentReads.Count;
        var missing = reader.Read();
        Require(!missing.Left.A.Available &&
            native.ComponentReads.Skip(callsBeforeMissing).All(read => read.Source == 202),
            "A missing hand is never passed to model or property getters");
        native.LeftIndex = 3;
        native.SourceFailure = true;
        reader.Invalidate();
        var missingSource = reader.Read();
        Require(!missingSource.Left.A.Available && missingSource.Left.Identity.InputSourceHandle == 0 &&
            missingSource.Right.A.Available,
            "Unresolved input sources cannot become all-device reads or suppress the other hand");
        native.SourceFailure = false;
        Require(reader.Read().Left.A.Available, "A failed hand-source lookup is retried without reconnecting gaze");

        native.Devices[3] = ("unknown", "index-left", "left-serial");
        reader.Invalidate();
        var unsupported = reader.Read();
        Require(!unsupported.Left.A.Available &&
            unsupported.Right.B.Available, "Unknown controller families never guess their A/B mappings");

        native.Devices[3] = ("oculus_touch", "oculus_cv1_controller_left", "left-serial");
        native.Devices[4] = ("oculus_touch", "oculus_cv1_controller_right", "right-serial");
        native.Components["oculus_cv1_controller_right"] = ["a_button", "b_button"];
        reader.Invalidate();
        var touch = reader.Read();
        Require(!touch.Left.A.Available && !touch.Left.B.Available && touch.Right.A.Available && touch.Right.B.Available &&
            native.ComponentReads.TakeLast(2).Select(read => read.Component).SequenceEqual(["a_button", "b_button"]),
            "CV1 right Touch maps physical A/B; left Touch X/Y do not become A/B");
        native.Devices[4] = ("oculus_touch", "oculus_quest2_controller_right", "right-serial");
        native.Components["oculus_quest2_controller_right"] = ["button_a", "button_b"];
        reader.Invalidate();
        var questTouch = reader.Read();
        Require(questTouch.Right.B.Available &&
            native.ComponentReads.TakeLast(2).Select(read => read.Component).SequenceEqual(["button_a", "button_b"]),
            "Quest Touch uses its verified button_a/button_b components");
        const string prefixedModel = "{oculus}oculus_quest2_controller_right";
        native.Devices[4] = ("oculus_touch", prefixedModel, "right-serial");
        native.Components[prefixedModel] = ["button_a", "button_b"];
        reader.Invalidate(4);
        var prefixedTouch = reader.Read();
        Require(prefixedTouch.Right.A.Available && prefixedTouch.Right.B.Available &&
            prefixedTouch.Right.Identity.RenderModel == prefixedModel && native.ComponentReads.Last().Model == prefixedModel,
            "Touch mapping recognizes the Oculus namespace and passes the original model name to the API");

        native.Devices[3] = ("knuckles", "index-left", "left-serial");
        native.Devices[4] = ("knuckles", "index-right", "right-serial");
        native.Components["index-left"] = ["button_b"];
        reader.Invalidate();
        var missingA = reader.Read();
        Require(!missingA.Left.A.Available &&
            missingA.Left.B.Available, "Missing model components disable only the affected button");
        native.PropertyFailure = true;
        reader.Invalidate();
        var propertyFailure = reader.Read();
        Require(!propertyFailure.Left.A.Available && propertyFailure.Left.Identity.RenderModel == "",
            "Failed model metadata reads cannot retain a previous model or pressed state");
        Require(!new ControllerButtonInput(native.System, native.Input, null).Read().Left.A.Available &&
            !new ControllerButtonInput(null, native.Input, native.Models).Read().Right.B.Available &&
            !new ControllerButtonInput(native.System, null, native.Models).Read().Left.B.Available,
            "Optional missing interfaces produce unavailable buttons without breaking gaze tracking");
        Require(native.Mutations == 0, "Controller polling never changes input bindings, actions, focus, or settings");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private sealed class FakeControllers : IDisposable
    {
        private readonly IVRSystem systemTable;
        private readonly IVRInput inputTable;
        private readonly IVRRenderModels modelTable;
        private readonly IntPtr systemPointer, inputPointer, modelPointer;
        internal CVRSystem System { get; }
        internal CVRInput Input { get; }
        internal CVRRenderModels Models { get; }
        internal uint LeftIndex = 3, RightIndex = 4;
        internal bool LeftConnected = true, SourceFailure, PropertyFailure, SerialFailure;
        internal int Reads, MetadataReads, ComponentEnumerations, Mutations;
        internal readonly Dictionary<uint, (string Type, string Model, string Serial)> Devices = new()
        {
            [3] = ("knuckles", "index-left", "left-serial"),
            [4] = ("knuckles", "index-right", "right-serial")
        };
        internal readonly Dictionary<string, string[]> Components = new()
        {
            ["index-left"] = ["button_a", "button_b"], ["index-right"] = ["button_a", "button_b"]
        };
        internal readonly Dictionary<(ulong Source, string Component), uint> Flags = new();
        internal readonly HashSet<(ulong Source, string Component)> Failures = new();
        internal readonly List<(ulong Source, string Component, string Model)> ComponentReads = new();

        internal FakeControllers()
        {
            systemTable = new IVRSystem
            {
                GetTrackedDeviceIndexForControllerRole = role =>
                    { Reads++; return role == ETrackedControllerRole.LeftHand ? LeftIndex : RightIndex; },
                IsTrackedDeviceConnected = index => { Reads++; return index != 3 || LeftConnected; },
                GetStringTrackedDeviceProperty = GetProperty,
                TriggerHapticPulse = (index, axis, duration) => Mutations++
            };
            inputTable = new IVRInput
            {
                GetInputSourceHandle = GetSource,
                SetActionManifestPath = path => { Mutations++; return EVRInputError.None; },
                UpdateActionState = (sets, size, count) => { Mutations++; return EVRInputError.None; }
            };
            modelTable = new IVRRenderModels
            {
                GetComponentCount = model =>
                {
                    Reads++; ComponentEnumerations++;
                    return (uint)Components.GetValueOrDefault(Marshal.PtrToStringUTF8(model), []).Length;
                },
                GetComponentName = GetComponentName,
                GetComponentStateForDevicePath = GetComponentState
            };
            systemPointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVRSystem>());
            inputPointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVRInput>());
            modelPointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVRRenderModels>());
            Marshal.StructureToPtr(systemTable, systemPointer, false);
            Marshal.StructureToPtr(inputTable, inputPointer, false);
            Marshal.StructureToPtr(modelTable, modelPointer, false);
            System = new CVRSystem(systemPointer);
            Input = new CVRInput(inputPointer);
            Models = new CVRRenderModels(modelPointer);
        }

        private uint GetProperty(uint index, ETrackedDeviceProperty property, StringBuilder value,
            uint capacity, ref ETrackedPropertyError error)
        {
            Reads++; MetadataReads++;
            if (!Devices.TryGetValue(index, out var device)) throw new InvalidOperationException("Invalid property device");
            string text = property switch
            {
                ETrackedDeviceProperty.Prop_ControllerType_String => device.Type,
                ETrackedDeviceProperty.Prop_RenderModelName_String => device.Model,
                ETrackedDeviceProperty.Prop_SerialNumber_String => device.Serial,
                _ => throw new InvalidOperationException("Unexpected property")
            };
            value.Append(text);
            error = PropertyFailure || (SerialFailure && property == ETrackedDeviceProperty.Prop_SerialNumber_String)
                ? ETrackedPropertyError.TrackedProp_UnknownProperty : ETrackedPropertyError.TrackedProp_Success;
            return (uint)text.Length + 1;
        }

        private EVRInputError GetSource(IntPtr path, ref ulong source)
        {
            Reads++;
            bool left = Marshal.PtrToStringUTF8(path) == "/user/hand/left";
            source = left ? 201UL : 202UL;
            return left && SourceFailure ? EVRInputError.NameNotFound : EVRInputError.None;
        }

        private uint GetComponentName(IntPtr model, uint index, StringBuilder name, uint capacity)
        {
            Reads++;
            string component = Components[Marshal.PtrToStringUTF8(model)][index];
            name.Append(component);
            return (uint)component.Length + 1;
        }

        private bool GetComponentState(IntPtr model, IntPtr component, ulong source,
            ref RenderModel_ControllerMode_State_t mode, ref RenderModel_ComponentState_t state)
        {
            Reads++;
            var key = (source, Marshal.PtrToStringUTF8(component));
            ComponentReads.Add((key.source, key.Item2, Marshal.PtrToStringUTF8(model)));
            if (source is not (201 or 202)) throw new InvalidOperationException("Missing explicit hand source");
            state.uProperties = Flags.GetValueOrDefault(key);
            return !Failures.Contains(key);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(systemPointer);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(modelPointer);
            GC.KeepAlive(systemTable);
            GC.KeepAlive(inputTable);
            GC.KeepAlive(modelTable);
        }
    }
}
