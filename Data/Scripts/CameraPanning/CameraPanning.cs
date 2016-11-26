using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace Digi.CameraPanning
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class CameraPanningMod : MySessionComponentBase
    {
        private bool init = false;

        private float originalCameraFovSmall = 0;
        private float originalCameraFovLarge = 0;

        private static readonly float CAMERA_FOV = MathHelper.ToRadians(100);
        private static readonly MyDefinitionId CAMERA_SMALL_ID = new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "SmallCameraBlock");
        private static readonly MyDefinitionId CAMERA_LARGE_ID = new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "LargeCameraBlock");

        public override void UpdateAfterSimulation()
        {
            if(init)
            {
                MyLog.Default.WriteLine("WARNING: " + GetType().Name + " still updates after the update method was disabled!");
                return;
            }

            try
            {
                if(MyAPIGateway.Session == null)
                    return;

                init = true;

                var def = GetCameraDefinition(CAMERA_SMALL_ID);

                if(def != null)
                {
                    originalCameraFovSmall = def.MaxFov;
                    def.MaxFov = CAMERA_FOV;
                }

                def = GetCameraDefinition(CAMERA_LARGE_ID);

                if(def != null)
                {
                    originalCameraFovLarge = def.MaxFov;
                    def.MaxFov = CAMERA_FOV;
                }

                // SetUpdateOrder() throws an exception if called in the update method; this to overcomes that
                MyAPIGateway.Utilities.InvokeOnGameThread(delegate ()
                {
                    SetUpdateOrder(MyUpdateOrder.NoUpdate);
                });
            }
            catch(Exception e)
            {
                MyAPIGateway.Utilities.ShowNotification("Error in " + GetType().Name + " - see SpaceEngineers.log", 5000, MyFontEnum.Red);
                MyLog.Default.WriteLineAndConsole(e.Message + "\n" + e.StackTrace);
            }
        }

        protected override void UnloadData()
        {
            try
            {
                // restore original FOV for camera definitions as they are not reloaded in between world loads which means removing the mod will not reset the FOV.
                var def = GetCameraDefinition(CAMERA_SMALL_ID);

                if(def != null)
                    def.MaxFov = originalCameraFovSmall;

                def = GetCameraDefinition(CAMERA_LARGE_ID);

                if(def != null)
                    def.MaxFov = originalCameraFovSmall;
            }
            catch(Exception e)
            {
                MyAPIGateway.Utilities.ShowNotification("Error in " + GetType().Name + " - see SpaceEngineers.log", 5000, MyFontEnum.Red);
                MyLog.Default.WriteLineAndConsole(e.Message + "\n" + e.StackTrace);
            }
        }

        private MyCameraBlockDefinition GetCameraDefinition(MyDefinitionId defId)
        {
            MyCubeBlockDefinition def;

            if(MyDefinitionManager.Static.TryGetCubeBlockDefinition(CAMERA_SMALL_ID, out def))
                return def as MyCameraBlockDefinition;

            return null;
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock))]
    public class CameraBlock : MyGameLogicComponent
    {
        private bool first = true;
        private bool controlling = false;
        private bool recenter = false;
        private Matrix originalMatrix;
        private Matrix rotatedMatrix;
        private Vector3 positionOffset;
        private float currentPitch = 0;
        private float currentYaw = 0;
        private float currentRoll = 0;
        private MyEntity3DSoundEmitter soundEmitter = null;

        private static IMyHudNotification notification = null;
        private static int notificationTimeoutTicks = 0;

        private const float SPEED_MUL = 0.1f; // rotation input multiplier as it is too fast raw compared to the rest of the game
        private const float MAX_SPEED = 1.8f; // using a max speed to feel like it's on actual servos
        private static readonly MySoundPair soundPair = new MySoundPair("BlockRotor"); // sound pair used for camera rotation, without the 'Arc' or 'Real' prefix.
        private const float EPSILON = 0.0001f;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
        }

        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(first)
                {
                    first = false;
                    originalMatrix = Entity.LocalMatrix;
                    rotatedMatrix = originalMatrix;

                    // HACK temporary fix for camera being in the center of the block
                    TryFixCameraPosition(); // moves the camera view's position towards the default mount point by gridSize/2.

                    if(soundEmitter == null)
                    {
                        soundEmitter = new MyEntity3DSoundEmitter((MyEntity)Entity);
                        soundEmitter.CustomVolume = 0.6f;
                    }
                }

                bool rotating = Update(); // returns true if the camera is rotating, false otherwise

                if(!rotating && soundEmitter != null && soundEmitter.IsPlaying)
                    soundEmitter.StopSound(true);
            }
            catch(Exception e)
            {
                MyAPIGateway.Utilities.ShowNotification("Error in " + GetType().Name + " - see SpaceEngineers.log", 5000, MyFontEnum.Red);
                MyLog.Default.WriteLineAndConsole(e.Message + "\n" + e.StackTrace);
            }
        }

        private bool Update()
        {
            if(MyAPIGateway.Session.CameraController != Entity)
            {
                if(controlling)
                {
                    controlling = false;
                    notificationTimeoutTicks = 150; // (int)((notification.AliveTime / 1000f) * 60f)

                    Entity.SetLocalMatrix(originalMatrix); // reset the camera's matrix to avoid seeing its model skewed if the model gets updated with the local matrix
                }

                return false;
            }

            if(notificationTimeoutTicks > 0)
                notificationTimeoutTicks--;

            var lookaroundControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.LOOKAROUND);
            var rotationTypeControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.SPRINT);
            var cameraModeControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.CAMERA_MODE);

            if(!controlling)
            {
                controlling = true; // only show this message once per camera control

                Entity.SetLocalMatrix(rotatedMatrix); // restore the last view matrix

                if(notification == null)
                    notification = MyAPIGateway.Utilities.CreateNotification("", 2500);

                notification.Text = "Hold " + GetControlAssignedName(lookaroundControl) + " to pan camera, " + GetControlAssignedName(lookaroundControl) + "+" + GetControlAssignedName(rotationTypeControl) + " to change rotation type and " + GetControlAssignedName(cameraModeControl) + " to reset.";
                notification.Show();
            }

            if(!recenter && cameraModeControl.IsNewPressed() && IsInputReadable()) // TODO cache this IsInputReadable() and check it in front, after it stops spewing exceptions
            {
                recenter = true;
                return false; // no reason to compute further
            }

            if(lookaroundControl.IsPressed() && IsInputReadable())
            {
                if(notificationTimeoutTicks > 0)
                {
                    notificationTimeoutTicks = 0;
                    notification.Hide();
                }

                var rot = MyAPIGateway.Input.GetRotation();
                bool rollToggle = rotationTypeControl.IsPressed();

                if(Math.Abs(rot.X) > EPSILON || Math.Abs(rot.Y) > EPSILON)
                {
                    if(recenter)
                        recenter = false;

                    if(rollToggle)
                    {
                        if(RotateCamera(rot.X * SPEED_MUL, MathHelper.Lerp(currentYaw, 0f, 0.1f), rot.Y * SPEED_MUL))
                            return true;
                    }
                    else
                    {
                        if(RotateCamera(rot.X * SPEED_MUL, rot.Y * SPEED_MUL, 0))
                            return true;
                    }
                }
                else if(rollToggle && Math.Abs(currentYaw) > EPSILON) // not moving mouse but holding modifier should result in yaw being slowly reset to 0
                {
                    if(RotateCamera(0, MathHelper.Lerp(currentYaw, 0f, 0.1f), 0))
                        return true;
                }
            }

            if(recenter)
            {
                if(Math.Abs(currentPitch) > EPSILON || Math.Abs(currentYaw) > EPSILON || Math.Abs(currentRoll) > EPSILON)
                {
                    if(RotateCamera(MathHelper.Lerp(currentPitch, 0f, 0.1f), MathHelper.Lerp(currentYaw, 0f, 0.1f), MathHelper.Lerp(currentRoll, 0f, 0.1f)))
                        return true;
                }
                else
                {
                    recenter = false;

                    // ensure it's perfectly centered
                    currentPitch = 0;
                    currentYaw = 0;
                    currentRoll = 0;
                    Entity.SetLocalMatrix(rotatedMatrix);
                    return true;
                }
            }

            return false;
        }

        private float ClampAngle(float value, float limit = 0)
        {
            if(limit > 0)
                value = MathHelper.Clamp(value, -limit, limit);

            if(value > 180)
                value = -180 + (value - 180);
            else if(value < -180)
                value = 180 - (value - 180);

            return value;
        }

        private bool RotateCamera(float pitchMod, float yawMod, float rollMod)
        {
            var camera = (IMyCameraBlock)Entity;
#if STABLE // HACK > STABLE CONDITION
            float angleLimit = 45f;
#else
            float angleLimit = camera.RaycastConeLimit;
#endif

            var setPitch = ClampAngle(currentPitch - MathHelper.Clamp(pitchMod, -MAX_SPEED, MAX_SPEED), angleLimit);
            var setYaw = ClampAngle(currentYaw - MathHelper.Clamp(yawMod, -MAX_SPEED, MAX_SPEED), angleLimit);
            var setRoll = ClampAngle(currentRoll - MathHelper.Clamp(rollMod, -MAX_SPEED, MAX_SPEED));

            if(Math.Abs(setPitch - currentPitch) >= 0.001f || Math.Abs(setYaw - currentYaw) >= 0.001f || Math.Abs(setRoll - currentRoll) >= 0.001f)
            {
                currentPitch = setPitch;
                currentYaw = setYaw;
                currentRoll = setRoll;

                rotatedMatrix = MatrixD.CreateFromYawPitchRoll(0, 0, MathHelper.ToRadians(currentRoll)) * originalMatrix;
                rotatedMatrix = MatrixD.CreateFromYawPitchRoll(MathHelper.ToRadians(currentYaw), MathHelper.ToRadians(currentPitch), 0) * rotatedMatrix;
                rotatedMatrix.Translation = originalMatrix.Translation + positionOffset;
                Entity.SetLocalMatrix(rotatedMatrix);

                if(soundEmitter != null && !soundEmitter.IsPlaying)
                    soundEmitter.PlaySound(soundPair, true, true);

                return true;
            }

            return false;
        }

        private static string GetControlAssignedName(IMyControl control)
        {
            var assign = control.GetControlButtonName(MyGuiInputDeviceEnum.Mouse);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            assign = control.GetControlButtonName(MyGuiInputDeviceEnum.Keyboard);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            assign = control.GetControlButtonName(MyGuiInputDeviceEnum.KeyboardSecond);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            return "(unassigned)";
        }

        private MyCubeBlockDefinition.MountPoint GetDefaultMountPoint(MyCubeBlock block)
        {
            var mountPoints = block.BlockDefinition.MountPoints;

            for(int i = 0; i < mountPoints.Length; i++)
            {
                var mount = mountPoints[i];

                if(mount.Enabled && mount.Default)
                    return mount;
            }

            if(mountPoints.Length > 0) // if none have the Default property set, the first one is assumed the default one
                return mountPoints[0];

            return default(MyCubeBlockDefinition.MountPoint);
        }

        private void TryFixCameraPosition()
        {
            var block = (MyCubeBlock)Entity;
            var mount = GetDefaultMountPoint(block);

            if(block.BlockDefinition.ModelOffset.LengthSquared() <= EPSILON) // ignore mods that have model offset because that also moves camera position
            {
                positionOffset = Vector3.TransformNormal((Vector3)mount.Normal, originalMatrix) * ((block.CubeGrid.GridSize / 2) + 0.05f);
                rotatedMatrix.Translation = originalMatrix.Translation + positionOffset;
                Entity.SetLocalMatrix(rotatedMatrix);
            }
        }

        private static bool IsInputReadable()
        {
            // TODO detect properly: escape menu, F10 and F11 menus, mission screens, yes/no notifications.

            var GUI = MyAPIGateway.Gui;

            if(GUI.ChatEntryVisible || GUI.GetCurrentScreen != MyTerminalPageEnum.None)
                return false;

            try // HACK ActiveGamePlayScreen throws NRE when called while not in a menu
            {
                return GUI.ActiveGamePlayScreen == null;
            }
            catch(Exception)
            {
                return true;
            }
        }
    }
}
