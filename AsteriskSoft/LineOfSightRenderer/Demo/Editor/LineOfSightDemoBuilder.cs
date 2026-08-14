// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Builds the demo scene with one menu click: ground, occluders (walls, a
// roofed structure, a tilted slab, a floating block), a player with the
// isometric controller and vision agent, a follow camera, and props with
// visibility trackers. Also installs and configures the renderer feature.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AsteriskSoft.LineOfSight.Demo.Editor
{
    public static class LineOfSightDemoBuilder
    {
        // Rendering layer bit 1 marks occluders; bit 0 stays set so default
        // lights keep affecting the objects.
        const uint kOccluderBit = 1u << 1;

        [MenuItem("Tools/Line of Sight/Create Demo Scene", priority = 60)]
        public static void CreateDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Ground (not an occluder)
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(8f, 1f, 8f);

            // Occluders
            CreateOccluder("Wall Long", new Vector3(-6f, 1.5f, 5f), new Vector3(10f, 3f, 0.35f), 0f);
            CreateOccluder("Wall Short", new Vector3(-11f, 1.5f, 0f), new Vector3(0.35f, 3f, 10f), 0f);
            CreateOccluder("Crate", new Vector3(4f, 0.5f, 2f), Vector3.one, 20f);
            CreateOccluder("Tilted Slab", new Vector3(9f, 2.6f, 6f), new Vector3(5f, 0.4f, 5f), 8f);
            CreateOccluder("Floating Block", new Vector3(3f, 4f, -5f), new Vector3(3f, 1f, 3f), 0f);

            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0) ? -2.5f : 2.5f;
                float z = (i < 2) ? -10.5f : -5.5f;
                CreateOccluder("Pillar " + (i + 1), new Vector3(x, 1.5f, z), new Vector3(0.4f, 3f, 0.4f), 0f);
            }
            CreateOccluder("Roof", new Vector3(0f, 3.2f, -8f), new Vector3(6.5f, 0.4f, 6.5f), 0f);

            // Player
            var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            player.transform.position = new Vector3(0f, 1.1f, 0f);
            Object.DestroyImmediate(player.GetComponent<Collider>());
            var controller = player.AddComponent<CharacterController>();
            controller.height = 2f;
            controller.center = Vector3.zero;
            var agent = player.AddComponent<LineOfSightAgent>();
            agent.eyeHeight = 1.6f;
            player.AddComponent<IsometricCharacterController>();

            // Camera
            var cam = Camera.main;
            if (cam != null)
            {
                var follow = cam.gameObject.AddComponent<IsometricCameraFollow>();
                follow.target = player.transform;
                cam.transform.position = player.transform.position + follow.offset;
                cam.transform.LookAt(player.transform.position + Vector3.up);
            }

            // Props with visibility trackers
            Vector3[] propPositions =
            {
                new Vector3(-8f, 0.75f, 8f),
                new Vector3(0f, 0.75f, -8f),
                new Vector3(7f, 0.75f, -1f),
                new Vector3(11f, 0.75f, 9f)
            };
            for (int i = 0; i < propPositions.Length; i++)
            {
                var prop = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                prop.name = "Tracked Prop " + (i + 1);
                prop.transform.position = propPositions[i];
                prop.transform.localScale = new Vector3(0.6f, 0.75f, 0.6f);
                prop.AddComponent<LineOfSightVisibilityTracker>();
            }

            // Renderer feature: install, then point it at the occluder layer.
            AsteriskSoft.LineOfSight.Editor.LineOfSightSetup.AddFeatureToActiveRenderer();
            foreach (var feature in Resources.FindObjectsOfTypeAll<LineOfSightRenderFeature>())
            {
                feature.settings.occluderRenderingLayers = kOccluderBit;
                EditorUtility.SetDirty(feature);
            }
            AsteriskSoft.LineOfSight.Editor.LineOfSightSetup.Validate();

            Debug.Log("[LOS] Demo scene created. Play in the Game view: WASD to move, mouse to aim. " +
                      "Tracked props show VISIBLE/HIDDEN labels. Save the scene before exporting the package.");
        }

        static GameObject CreateOccluder(string name, Vector3 position, Vector3 scale, float tiltDegrees)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
            if (tiltDegrees != 0f)
                go.transform.rotation = Quaternion.Euler(tiltDegrees, 25f, 0f);
            go.GetComponent<Renderer>().renderingLayerMask = 1u | kOccluderBit;
            return go;
        }
    }
}
