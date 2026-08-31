using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace MajdataViewX.Managers
{
    [RequireComponent(typeof(Text))]
    public class ErrorEffect : MonoBehaviour
    {
        [SerializeField]
        private ErrorCharacterPool _characterPool;

        [Tooltip("If true, uses unscaled time (unaffected by Time.timeScale)")]
        [SerializeField]
        private bool _useUnscaledTime = false;

        [Tooltip("Default allowed state. Leave off if this should always be driven externally.")]
        [SerializeField]
        private bool _startAllowed = false;

        private Text _text;
        private int _length;
        private bool _allowed;
        private Coroutine _errorRoutine;

        private void Awake()
        {
            _text = GetComponent<Text>();
            _allowed = _startAllowed;
        }

        private void OnEnable()
        {
            SyncLengthFromCurrentText();
            TryStartError();
        }

        private void OnDisable()
        {
            StopError();
        }

        public void SetErrorAllowed(bool allowed)
        {
            _allowed = allowed;

            if (!allowed)
            {
                StopError();
                return;
            }

            if (isActiveAndEnabled)
            {
                SyncLengthFromCurrentText();
                TryStartError();
            }
        }


        // Call this manually if the text is changed while this component is already enabled.

        public void SyncLengthFromCurrentText()
        {
            _length = _text.text?.Length ?? 0;
        }

        private void TryStartError()
        {
            if (!_allowed || _length <= 0)
                return;

            if (_errorRoutine == null)
                _errorRoutine = StartCoroutine(ErrorLoop());
        }

        private void StopError()
        {
            if (_errorRoutine != null)
            {
                StopCoroutine(_errorRoutine);
                _errorRoutine = null;
            }
        }

        private IEnumerator ErrorLoop()
        {
            while (true)
            {
                CycleCharacters();

                var interval = _characterPool != null ? _characterPool.CycleInterval : 0.05f;

                if (_useUnscaledTime)
                    yield return new WaitForSecondsRealtime(interval);
                else
                    yield return new WaitForSeconds(interval);
            }
        }

        private void CycleCharacters()
        {
            var pool = _characterPool != null ? _characterPool.Pool : null;

            if (_length <= 0 || string.IsNullOrEmpty(pool))
                return;

            var buffer = new char[_length];
            for (var i = 0; i < _length; i++)
            {
                buffer[i] = pool[UnityEngine.Random.Range(0, pool.Length)];
            }

            _text.text = new string(buffer);
        }
    }
}