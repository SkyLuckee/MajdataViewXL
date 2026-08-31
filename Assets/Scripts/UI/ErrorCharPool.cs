using UnityEngine;

namespace MajdataViewX.Managers
{
    /// <summary>
    /// Shared character pool for ErrorEffect. Create one asset (Assets > Create >
    /// MajdataViewX > Glitch Character Pool) and reference the same asset from every
    /// ErrorEffect that should draw from the same pool -- editing this asset
    /// updates all of them at once, no per-component duplication.
    /// </summary>
    [CreateAssetMenu(fileName = "ErrorCharacterPool", menuName = "MajdataViewX/Error Character Pool")]
    public class ErrorCharacterPool : ScriptableObject
    {
        [Tooltip("Characters that may be randomly picked for each glitched position, including for original spaces.")]
        [TextArea(2, 4)]
        public string Pool = "縺縺縺縺縺縺縺縺ヲイ@&?>¥九ク、%h上サ◆∴∅√≠·帙医※〇☆ゆヨf≥才∩a薙→↑↓←■▽b⌾";

        [Tooltip("Seconds between each full glitch cycle. Shared by every ErrorEffect referencing this asset.")]
        public float CycleInterval = 0.25f;
    }
}