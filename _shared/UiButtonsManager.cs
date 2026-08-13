// Implements: refactor-input-system-memora.md — Cluster E2 (UiButtonsManager WaitUntil pattern)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Localization;


public enum HintIconType
{
    EIcon,
    LeftClickIcon,
    RightClickIcon,
    MiddleMouseIcon,
    RIcon,
    QIcon,
    WASDIcon,
    SpaceIcon,
    FIcon,
    ShiftIcon
}

public class UIButtonsManager : MonoBehaviour
{
    public static UIButtonsManager instance { get; private set; }

    [SerializeField] private Transform canvas;
    [SerializeField] private Transform hintContainer;
    [SerializeField] private GameObject hintPrefab;
    [SerializeField] private List<HintIconEntry> hintIconEntriesList;
    private Dictionary<HintIconType, Sprite> hintIconsDictionary;
    private Coroutine tutorialCoroutine;

    private readonly List<HintInstance> currentHints = new();
    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            //DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }

        canvas.gameObject.SetActive(false);
        hintIconsDictionary = hintIconEntriesList.ToDictionary(entry => entry.type, entry => entry.icon);

    }

    public void ShowHints(List<HintDisplayData> hints)
    {
        if(tutorialCoroutine != null)
        {
            StopCoroutine(tutorialCoroutine);
            tutorialCoroutine = null;
        }
        ClearHints();
        canvas.gameObject.SetActive(true);

        for (int i = 0; i < hints.Count; i++)
        {
            HintDisplayData hint = hints[i];
            GameObject entry = Instantiate(hintPrefab, hintContainer);
            entry.GetComponent<HintPrefab>().Setup(hint.icons, hint.localizedText);
            currentHints.Add(new HintInstance
            {
                prefab = entry,
                data = hint
            });
        }
    }

    public HintInstance AddAdditionalHint(HintDisplayData hintData)
    {
        if (currentHints.Any(h => h.data.localizedText == hintData.localizedText))
            return null;

        GameObject entry = Instantiate(hintPrefab, hintContainer);
        entry.GetComponent<HintPrefab>().Setup(hintData.icons, hintData.localizedText);

        var newHint = new HintInstance
        {
            prefab = entry,
            data = hintData
        };

        currentHints.Add(newHint);

        canvas.gameObject.SetActive(true);
        return newHint;
    }

    public void RemoveAdditionalHint(HintInstance hintInstance)
    {
        if (hintInstance == null) return;

        // Guard: un ShowHints() de un modo modal hace ClearHints() y destruye este prefab por
        // detrás, dejando colgada la referencia que guardó quien llamó a AddAdditionalHint.
        // Sin esto, el Destroy y el Remove de abajo operarían sobre una instancia ya muerta.
        if (hintInstance.prefab == null)
        {
            currentHints.Remove(hintInstance);
            return;
        }

        Destroy(hintInstance.prefab);
        currentHints.Remove(hintInstance);
        if (currentHints.Count == 0)
            canvas.gameObject.SetActive(false);
    }

    private IEnumerator TutorialCoroutine(List<HintDisplayData> hints, float duration)
    {
        ShowHints(hints);
        yield return new WaitForSeconds(duration);
        HideHints();
    }

    // Replaces WaitUntil(()=>Input.GetKeyDown(keyCode)) with InputHandler.WaitForAction.
    // mapName/actionName must match a valid action in PlayerControls.inputactions.
    // Falls back to hiding immediately if InputHandler is unavailable (e.g. in editor tests).
    private IEnumerator TutorialWaitForKeyPressedCoroutine(List<HintDisplayData> hints, string mapName, string actionName)
    {
        ShowHints(hints);
        yield return null; // espera 1 frame para evitar key press previos
        if (InputHandler.instance != null)
            yield return InputHandler.instance.WaitForAction(mapName, actionName);
        HideHints();
    }

    /// <param name="duration">Segundos en pantalla. Corto (2-3 s) para recordatorios en pleno momento de acción;
    /// el default de 5 s es para los tutoriales del arranque, donde el jugador está tranquilo leyendo.</param>
    public void Tutorial(List<HintDisplayData> hints, float duration = 5f)
    {
        if (tutorialCoroutine != null) StopCoroutine(tutorialCoroutine);
        tutorialCoroutine = StartCoroutine(TutorialCoroutine(hints, duration));
    }

    public void TutorialWaitForKeyPressed(List<HintDisplayData> hints, string mapName, string actionName)
    {
        tutorialCoroutine = StartCoroutine(TutorialWaitForKeyPressedCoroutine(hints, mapName, actionName));
    }

    public void HideHints()
    {
        ClearHints();
        canvas.gameObject.SetActive(false);
    }

    private void ClearHints()
    {
        for (int i = 0; i < currentHints.Count; i++)
        {
            Destroy(currentHints[i].prefab);
        }
        currentHints.Clear();
    }

    public Sprite GetIcon(HintIconType type)
    {
        hintIconsDictionary.TryGetValue(type, out var sprite);
        return sprite;
    }

}


[Serializable]
public class HintIconEntry
{
    public HintIconType type;
    public Sprite icon;
}

[Serializable]
public class HintDisplayData
{
    /// <summary>
    /// Input icon sprites rendered left-to-right before the localized label.
    /// Build via <see cref="Make"/> — do not use object-initializer syntax directly.
    /// </summary>
    public List<Sprite> icons;

    /// <summary>Localized label displayed after the icons.</summary>
    public LocalizedString localizedText;

    /// <summary>
    /// Primary factory. Resolves icon sprites from <see cref="UIButtonsManager"/> and
    /// produces a ready-to-display hint in one line.
    ///
    /// Single icon:  HintDisplayData.Make(HintType.Exit, HintIconType.RightClickIcon)
    /// Multi-icon:   HintDisplayData.Make(HintType.ChangeTab, HintIconType.EIcon, HintIconType.QIcon)
    ///
    /// Constraint: UIButtonsManager.instance must exist at call time — the same
    /// constraint every existing call site already relied on via GetIcon().
    /// </summary>
    public static HintDisplayData Make(HintType textKey, params HintIconType[] iconTypes)
    {
        var resolvedIcons = new List<Sprite>(iconTypes.Length);
        for (int i = 0; i < iconTypes.Length; i++)
            resolvedIcons.Add(UIButtonsManager.instance.GetIcon(iconTypes[i]));

        return new HintDisplayData
        {
            icons = resolvedIcons,
            localizedText = UiHintLocalization.Get(textKey)
        };
    }
}

[Serializable]
public class HintInstance
{
    public GameObject prefab;
    public HintDisplayData data;
}
