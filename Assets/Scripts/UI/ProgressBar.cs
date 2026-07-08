using UnityEngine;
using UnityEngine.UI;

public class ProgressBar : MonoBehaviour
{
    [SerializeField] private Image mask;

    private TimeProvider timeProvider;
    private AudioManager audioManager;
    private float currentTiming;
    private float lastTiming = 1f;

    private void Awake()
    {

    }

    private void Start()
    {
        RefreshManagers();
    }

    private void Update()
    {   
        var screenAspect = (float)Screen.width / Screen.height;
        RectTransform rectTransform = GetComponent<RectTransform>();
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (float)(172.973f*(screenAspect/1.777777777777778f)));
        UpdateFromSongProgress();
        
    }

    private void RefreshManagers()
    {
        if (timeProvider == null && Majdata<TimeProvider>.Instance != null)
        {
            timeProvider = Majdata<TimeProvider>.Instance;
        }

        if (audioManager == null && Majdata<AudioManager>.Instance != null)
        {
            audioManager = Majdata<AudioManager>.Instance;
        }
    }

    private void UpdateFromSongProgress()
    {
        if (timeProvider != null)
        {
            currentTiming = timeProvider.AudioTime;
        }

        if (audioManager != null)
        {
            lastTiming = audioManager.TrackLengthSeconds;
        }

        if (lastTiming <= 0f)
        {
            mask.fillAmount = currentTiming > 0f ? 1f : 0f;
            return;
        }

        mask.fillAmount = Mathf.Clamp01(currentTiming / lastTiming);
    }
}
