using System;
using System.Linq;
using Dories.Audio.Runtime;
using UnityEditor;
using UnityEngine;

namespace Dories.Audio.Editor
{
    [CustomEditor(typeof(AudioEntity))]
    public class AudioEntityInspector : UnityEditor.Editor
    {
        private const string ProviderField = "_clipProvider";

        private Type[] _providerTypes;
        private string[] _providerNames;
        private Vector2 _soundScroll;
        private Vector2 _clipScroll;
        private bool _monitorFoldout = true;

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, ProviderField);
            DrawProviderSelector(serializedObject.FindProperty(ProviderField));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            DrawMonitor();
        }

        private void DrawProviderSelector(SerializedProperty property)
        {
            if (_providerTypes == null)
            {
                // SerializeReference 只能放可序列化的非 UnityEngine.Object 类型，且下拉创建需要无参构造
                _providerTypes = TypeCache.GetTypesDerivedFrom<IAudioClipProvider>()
                    .Where(t => !t.IsAbstract && !t.IsGenericType && t.IsSerializable
                                && !typeof(UnityEngine.Object).IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.Name)
                    .ToArray();
                _providerNames = new[] { "(自动)" }.Concat(_providerTypes.Select(t => t.Name)).ToArray();
            }

            // managedReferenceFullTypename 格式为 "程序集名 完整类型名"
            var current = property.managedReferenceFullTypename;
            int index = Array.FindIndex(_providerTypes, t => $"{t.Assembly.GetName().Name} {t.FullName}" == current) + 1;

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                int selected = EditorGUILayout.Popup("Clip Provider", index, _providerNames);
                if (selected != index)
                    property.managedReferenceValue = selected == 0 ? null : Activator.CreateInstance(_providerTypes[selected - 1]);
            }

            if (property.managedReferenceValue == null)
                return;

            EditorGUI.indentLevel++;
            var end = property.GetEndProperty();
            var child = property.Copy();
            for (bool enter = true; child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end); enter = false)
                EditorGUILayout.PropertyField(child, true);
            EditorGUI.indentLevel--;
        }

        private void DrawMonitor()
        {
            _monitorFoldout = EditorGUILayout.Foldout(_monitorFoldout, "Monitor", true);
            if (!_monitorFoldout)
                return;

            EditorGUI.indentLevel++;

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("进入 Play Mode 后显示播放与缓存状态。", MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            var entity = (AudioEntity)target;
            var track = entity.CurrentMusicTrack;

            EditorGUILayout.LabelField("ClipProvider", entity.ClipProvider?.GetType().Name ?? "(未设置)");
            EditorGUILayout.LabelField("Music", track.Key is { } musicKey
                ? $"{AudioEntity.DisplayName(musicKey)}  ({track.Fade:P0})"
                : "(无)");
            EditorGUILayout.LabelField("播放中音效", entity.ActiveSoundCount.ToString());

            if (entity.Sounds.Count > 0)
            {
                _soundScroll = EditorGUILayout.BeginScrollView(_soundScroll, GUILayout.MaxHeight(140f));
                foreach (var sound in entity.Sounds)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label($"#{sound.Id}", GUILayout.Width(48f));
                    GUILayout.Label(AudioEntity.DisplayName(sound.Key));
                    GUILayout.Label(sound.Source != null && sound.Source.loop ? "Loop" : "", GUILayout.Width(40f));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("缓存 AudioClip", entity.Clips.Count.ToString());
            if (entity.Clips.Count > 0)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                EditorGUILayout.LabelField("Location", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Ref", EditorStyles.miniLabel, GUILayout.Width(40f));
                EditorGUILayout.LabelField("Idle", EditorStyles.miniLabel, GUILayout.Width(48f));
                EditorGUILayout.EndHorizontal();

                float now = Time.unscaledTime;
                _clipScroll = EditorGUILayout.BeginScrollView(_clipScroll, GUILayout.MaxHeight(200f));
                foreach (var pair in entity.Clips)
                {
                    var entry = pair.Value;
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(AudioEntity.DisplayName(pair.Key));
                    GUILayout.Label(entry.RefCount.ToString(), GUILayout.Width(40f));
                    GUILayout.Label(entry.KeepAlive ? "Keep" : entry.RefCount > 0 ? "-" : $"{now - entry.LastUseTime:F0}s", GUILayout.Width(48f));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Stop All Sounds"))
                entity.StopAllSounds();
            if (GUILayout.Button("Release Unused Clips"))
                entity.ReleaseUnusedClips();
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }
    }
}
