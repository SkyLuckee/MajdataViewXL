using UnityEngine;
using UnityEngine.UI;
using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX.UI
{
    [RequireComponent(typeof(RectTransform))]
    public class SongProgressBar : MonoBehaviour
    {
        [Tooltip("Image with Image Type = Filled, Fill Method = Horizontal")]
        [SerializeField] private Image fillImage;

        [Tooltip("Left/right margin in pixels, kept when stretching to screen width")]
        [SerializeField] private float horizontalMargin = 0f;

        private RectTransform rectTransform;

        private void Awake()
        {
            
        }

        private void StretchToScreenWidth()
        {
            var screenAspect = (float)Screen.width / Screen.height;
            rectTransform = GetComponent<RectTransform>();
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (float)(172.973f*(screenAspect/1.777777777777778f)));
        }

        private void Update()
        {
            if (fillImage == null) return;
            StretchToScreenWidth();

            var length = _audioManager?.TrackLengthSec ?? 0;
            if (length <= 0)
            {
                fillImage.fillAmount = 0f;
                return;
            }

            var progress = _timeProvider.AudioTime / (float)length;
            fillImage.fillAmount = Mathf.Clamp01(progress);
        }
    }
}