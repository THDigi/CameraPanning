using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRageMath;

namespace Digi.CameraPanning
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class CameraPanningMod : MySessionComponentBase
    {
        public static CameraPanningMod Instance = null;
        public IMyHudNotification Notification;
        public Dictionary<MyDefinitionId, ZoomLimits> WidthLimits;

        private float OriginalCameraFovSmall = 0;
        private float OriginalCameraFovLarge = 0;

        public const float CAMERA_NEW_MAX_FOV = (float)(100 / 180d * Math.PI); // 100 degrees in radians
        public readonly MyDefinitionId CAMERA_SMALL_ID = new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "SmallCameraBlock");
        public readonly MyDefinitionId CAMERA_LARGE_ID = new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "LargeCameraBlock");

        public override void LoadData()
        {
            Instance = this;
            Log.ModName = "Camera Panning";
            Log.AutoClose = false;
        }

        public override void BeforeStart()
        {
            try
            {
                if(!MyAPIGateway.Utilities.IsDedicated)
                {
                    EditVanillaMaxFov();
                    StoreFovLimits();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void EditVanillaMaxFov()
        {
            var def = GetCameraDefinition(CAMERA_SMALL_ID);

            if(def != null)
            {
                OriginalCameraFovSmall = def.MaxFov;
                def.MaxFov = CAMERA_NEW_MAX_FOV;
            }

            def = GetCameraDefinition(CAMERA_LARGE_ID);

            if(def != null)
            {
                OriginalCameraFovLarge = def.MaxFov;
                def.MaxFov = CAMERA_NEW_MAX_FOV;
            }
        }

        private void StoreFovLimits()
        {
            WidthLimits = new Dictionary<MyDefinitionId, ZoomLimits>(MyDefinitionId.Comparer);

            foreach(var def in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var camDef = def as MyCameraBlockDefinition;

                if(camDef != null)
                {
                    WidthLimits[def.Id] = new ZoomLimits(camDef.MinFov, camDef.MaxFov);
                }
            }
        }

        public override void HandleInput()
        {
            try
            {
                if(MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible)
                    return;

                HandleCameraZoom();
                HandleResetFirstPersonView();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void HandleCameraZoom()
        {
            var cameraBlock = MyAPIGateway.Session?.CameraController as IMyCameraBlock;
            var logic = cameraBlock?.GameLogic?.GetAs<CameraBlock>();

            if(logic != null)
            {
                int scroll = MyAPIGateway.Input.DeltaMouseScrollWheelValue();

                if(scroll > 0)
                {
                    logic.ZoomIn();
                }
                else if(scroll < 0)
                {
                    logic.ZoomOut();
                }
            }
        }

        private void HandleResetFirstPersonView()
        {
            // Reset view when forced in first person by pressing the camera key
            if(MyAPIGateway.Input.IsNewGameControlPressed(MyControlsSpace.CAMERA_MODE))
            {
                var camCtrl = MyAPIGateway.Session.CameraController;
                var controller = MyAPIGateway.Session.ControlledObject as Sandbox.Game.Entities.IMyControllableEntity; // avoiding ambiguity

                if(camCtrl == null || controller == null)
                    return;

                if(!MyAPIGateway.Session.SessionSettings.Enable3rdPersonView || controller.ForceFirstPersonCamera)
                {
                    if(controller is IMyShipController)
                    {
                        // HACK this is how MyCockpit.Rotate() does things so I kinda have to use these magic numbers.
                        var num = MyAPIGateway.Input.GetMouseSensitivity() * 0.13f;
                        camCtrl.Rotate(new Vector2(controller.HeadLocalXAngle / num, controller.HeadLocalYAngle / num), 0);
                    }
                    else
                    {
                        // HACK this is how MyCharacter.RotateHead() does things so I kinda have to use these magic numbers.
                        camCtrl.Rotate(new Vector2(controller.HeadLocalXAngle * 2, controller.HeadLocalYAngle * 2), 0);
                    }
                }
            }
        }

        protected override void UnloadData()
        {
            try
            {
                if(!MyAPIGateway.Utilities.IsDedicated)
                {
                    // restore original FOV for camera definitions as they are not reloaded in between world loads which means removing the mod will not reset the FOV.
                    var def = GetCameraDefinition(CAMERA_SMALL_ID);

                    if(def != null)
                        def.MaxFov = OriginalCameraFovSmall;

                    def = GetCameraDefinition(CAMERA_LARGE_ID);

                    if(def != null)
                        def.MaxFov = OriginalCameraFovLarge;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }

            Instance = null;
            Log.Close();
        }

        private MyCameraBlockDefinition GetCameraDefinition(MyDefinitionId defId)
        {
            MyCubeBlockDefinition def;

            if(MyDefinitionManager.Static.TryGetCubeBlockDefinition(defId, out def))
                return def as MyCameraBlockDefinition;

            return null;
        }
    }
}
