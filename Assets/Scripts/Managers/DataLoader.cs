#nullable enable

using Cysharp.Threading.Tasks;
using MajSimai;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using TMPro;

using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX.Managers
{
    public class DataLoader : MonoBehaviour
    {
        //serialized field
        public Text diffText;
        public Text levelText;
        public Text titleText;
        public Text artistText;
        public Text designText;
        public RawImage cardImage;
        public Color[] diffColors = new Color[7];
        public TextMeshProUGUI levelTextM;
        public Text titleTextM;
        public Text artistTextM;
        public Text designTextM;
        public Text bpmTextM;
        public SpriteRenderer cardImageM;
        public SpriteRenderer LvBackgroundM;
        public SpriteRenderer[] TabM = new SpriteRenderer[2];
        public GameObject[] Modes = new GameObject[2];
        public Sprite[] cardImagesM = new Sprite[8];
        public Sprite[] LvBackgroundsM = new Sprite[8];
        public Sprite[] TabsM = new Sprite[8];
        public Texture2D[] MLevelsM = new Texture2D[8];
        public GameObject QuestionM;
        public GameObject TabUTGM;
        public Text UTGTextM;
        // public GameObject TabUTG2pM;
        public SpriteRenderer[] BGLayers = new SpriteRenderer[11];
        public RawImage BGM;
        public TextMeshProUGUI NOTESDESIGNER;
        public Material defaultMaterial;
        public Material grayScaleMaterial;

        private void Awake()
        {
            _dataLoader = this;
        }

        public async UniTask Load(
            SimaiChart chart,
            SimaiFile file,
            int diff)
        {
            var title = file.Title;
            var artist = file.Artist;
            var commands = file.Commands;
            
            titleText.text = title;
            artistText.text = artist;
            diffText.text = GetDifficultyText(diff);
            cardImage.color = diffColors[diff];
            levelText.text = chart.Level;
            designText.text = chart.Designer;

            //MaiUI        
            bool grayScale = false; // GrayScale command
            var grayScaleCommand = commands.FirstOrDefault(c => c.Prefix?.Trim() == "gray_scale");
            if (grayScaleCommand != default) bool.TryParse(grayScaleCommand.Value, out grayScale);            

            levelTextM.spriteAsset.spriteSheet = (grayScale) ? MLevelsM[7] : MLevelsM[diff];
            levelTextM.spriteAsset.material.SetTexture("_MainTex", (grayScale) ? MLevelsM[7] : MLevelsM[diff]); // use DAMMY for text

            UTGTextM.text = "";
            TabUTGM.SetActive(false);

            string levelStr = chart.Level;

            StringBuilder sb = new();
            if (levelStr.StartsWith('['))
            {
                var last = levelStr.LastIndexOf(']');

                if (last > 1) // Guard against empty brackets
                {
                    TabUTGM.SetActive(true);
                    UTGTextM.text = levelStr[1..last];
                    levelStr = levelStr.Replace(levelStr[0..(last+1)], "");
                }
            }

            if (levelStr.Length == 1)
            {
                sb.Append("<space=1>");
            }
            foreach (var item in levelStr)
            {
                if (int.TryParse(item.ToString(), out int lv))
                    sb.Append($"<sprite={lv}>");
                else
                {
                    switch (item)
                    {
                        case '+':
                            sb.Append("<sprite=10>");
                            break;
                        case '-':
                            sb.Append("<sprite=11>");
                            break;
                        case ',':
                            sb.Append("<sprite=12>");
                            break;
                        case '.':
                            sb.Append("<sprite=13>");
                            break;
                    }
                }
            }
            levelTextM.text = sb.ToString();
            titleTextM.text = title;
            artistTextM.text = artist;
            designTextM.text = chart.Designer;
            designTextM.color = (grayScale) ? Color.black : new Color(0.480320f, 0.576780f, 0.750943f, 1f);
            if (!chart.NoteTimings.IsEmpty)
            {
                bpmTextM.text = "BPM " + chart.NoteTimings[0].Bpm;
                bpmTextM.color = (grayScale) ? Color.black : new Color(0.350181f, 0.412731f, 0.516981f, 1f);
            }
            NOTESDESIGNER.color = (grayScale) ? Color.black : new Color(0.421851f, 0.537755f, 0.675471f, 1f);

            cardImageM.sprite = cardImagesM[diff];
            cardImageM.material = (grayScale) ? grayScaleMaterial : defaultMaterial;
            LvBackgroundM.sprite = LvBackgroundsM[diff];
            LvBackgroundM.material = (grayScale) ? grayScaleMaterial : defaultMaterial;

            // GrayScale elements
            if (grayScale)
            {
                BGM.material = grayScaleMaterial;
                foreach (var r in BGLayers)
                {
                    r.material = grayScaleMaterial;
                }
            }
            else
            {
                BGM.material = defaultMaterial;
                foreach (var r in BGLayers)
                {
                    r.material = defaultMaterial;
                }
            }

            // STD/DX command
            var chartMode = "DX";
            var chartModeCommand = commands.FirstOrDefault(c => c.Prefix?.Trim() == "chart_mode");
            if (chartModeCommand != default) chartMode = chartModeCommand.Value?.Trim() ?? chartMode;

            if (diff != 6)
            {
                if (chartMode == "STD")
                {
                    Modes[0].SetActive(true);
                    Modes[0].GetComponent<SpriteRenderer>().material = (grayScale) ? grayScaleMaterial : defaultMaterial;
                    Modes[1].SetActive(false);
                    TabM[0].sprite = TabsM[diff];
                    TabM[0].material = (grayScale) ? grayScaleMaterial : defaultMaterial;
                }
                else
                {
                    Modes[0].SetActive(false);
                    Modes[1].SetActive(true);
                    Modes[1].GetComponent<SpriteRenderer>().material = (grayScale) ? grayScaleMaterial : defaultMaterial;
                    TabM[1].sprite = TabsM[diff];
                    TabM[1].material = (grayScale) ? grayScaleMaterial : defaultMaterial;
                }
            }
            else
            {
                Modes[0].SetActive(false);
                Modes[1].SetActive(false);
                TabM[0].gameObject.SetActive(false);
                TabM[1].gameObject.SetActive(false);
            }

            QuestionM.SetActive(levelStr.EndsWith('?'));
            QuestionM.GetComponent<SpriteRenderer>().material = (grayScale) ? grayScaleMaterial : defaultMaterial;
            levelStr = levelStr.Replace("?", "");

            _timeProvider.LoadSV(chart.CommaTimings);

            _noteManager.Load(chart);

            await UniTask.Yield();
        }

        private static string GetDifficultyText(int index) =>
            index switch
            {
                0 => "EASY",
                1 => "BASIC",
                2 => "ADVANCED",
                3 => "EXPERT",
                4 => "MASTER",
                5 => "Re:MASTER",
                6 => "ORIGINAL",
                _ => "DEFAULT"
            };
    }
}