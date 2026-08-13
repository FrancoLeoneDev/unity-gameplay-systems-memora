using DG.Tweening;
using System;
using System.Collections;
using UnityEngine;

public class MoveCamPuzzles : MonoBehaviour
{
    public event Action OnCameraMoved;
    public event Action OnCameraBack;

    public static MoveCamPuzzles Instance { get; private set; }

    [SerializeField] private float moveSpeed = 1f;
    [SerializeField] private Transform parentCam;

    private PlayerCam playerCam;
    private bool isMoving;
    private bool moved;

    public bool IsMoving => isMoving;

    private void Awake()
    {
        Instance = this;
        playerCam = GetComponentInChildren<PlayerCam>();
    }

    #region Movement

    public void SetCamAtParent()
    {
        transform.position = parentCam.position;
    }

    public void LookAtWithAnimation(Vector3 targetForward, float duration)
    {
        Quaternion targetRotation = Quaternion.LookRotation(targetForward);
        transform.DORotateQuaternion(targetRotation, duration).SetEase(Ease.OutQuad);
    }

    public void MoveToPosition(Transform targetPosition, bool light)
    {
        if (isMoving) return;
        isMoving = true;

        if (PlayerManager.instance != null)
            PlayerManager.instance.CanMove(false);

        OnCameraMoved?.Invoke();

        DOTween.Sequence()
            .Append(transform.DOMove(targetPosition.position, moveSpeed).SetEase(Ease.OutQuad))
            .Join(transform.DORotateQuaternion(targetPosition.rotation, moveSpeed).SetEase(Ease.OutQuad))
            .OnComplete(() =>
            {
                InspectionLightService.SetActive(light);
                isMoving = false;
                moved = true;
            });
    }

    public void LookSideways(Transform targetRotation, float duration = 1.5f)
    {
        if (isMoving) return;
        isMoving = true;
        playerCam.enabled = false;

        transform.DORotateQuaternion(targetRotation.rotation, duration).SetEase(Ease.OutQuad).OnComplete(() =>
        {
            playerCam.SyncFromCurrentCamPosAndReactive();
            isMoving = false;
        });
    }

    public void ResetPosition(Action<bool> onComplete)
    {
        if (isMoving || !moved)
        {
            onComplete?.Invoke(false);
            return;
        }

        isMoving = true;
        InspectionLightService.SetActive(false);

        DOTween.Sequence()
            .Append(transform.DOMove(parentCam.position, moveSpeed).SetEase(Ease.OutQuad))
            .Join(transform.DORotateQuaternion(parentCam.rotation, moveSpeed).SetEase(Ease.OutQuad))
            .OnComplete(() =>
            {
                if (PlayerManager.instance != null)
                    PlayerManager.instance.CanMove(true);

                isMoving = false;
                moved = false;
                onComplete?.Invoke(true);
            });
    }

    public void MoveToPositionInstant(Transform target, bool light, float intensity = -1f)
    {
        MoveToPoseInstant(target.position, target.rotation, light, intensity: intensity);
    }

    /// <summary>
    /// Fade a negro → teletransporte del rig a una pose arbitraria → fade de vuelta.
    /// No requiere Transform autorado (DocumentReader la usa con encuadres calculados).
    /// <paramref name="whileBlack"/> se invoca con la pantalla en negro (tras el teleport):
    /// ideal para mostrar UI o alterar la escena sin que se vea el cambio.
    /// <paramref name="onArrived"/> se invoca al completar el fade de vuelta.
    /// </summary>
    public void MoveToPoseInstant(Vector3 position, Quaternion rotation, bool light, Action whileBlack = null, Action onArrived = null, float intensity = -1f)
    {
        StartCoroutine(FadeAndMovePose(position, rotation, light, whileBlack, onArrived, intensity));
        OnCameraMoved?.Invoke();
        moved = true;
    }

    private IEnumerator FadeAndMovePose(Vector3 position, Quaternion rotation, bool light, Action whileBlack, Action onArrived, float intensity)
    {
        yield return StartCoroutine(FadeManager.instance.FadeIn());

        if (PlayerManager.instance != null)
            PlayerManager.instance.CanMove(false);

        transform.position = position;
        transform.rotation = rotation;

        // La luz se enciende en negro: el pop de iluminación nunca se ve.
        InspectionLightService.SetActive(light, intensity);
        whileBlack?.Invoke();

        yield return StartCoroutine(FadeManager.instance.FadeOut());

        onArrived?.Invoke();
    }

    public IEnumerator BackToOriginPosInstant(Action whileBlack = null)
    {
        if (!moved) yield break;

        yield return StartCoroutine(FadeManager.instance.FadeIn());

        OnCameraBack?.Invoke();
        whileBlack?.Invoke();

        transform.position = parentCam.position;
        transform.rotation = parentCam.rotation;

        if (PlayerManager.instance != null)
            PlayerManager.instance.CanMove(true);

        if (GameManager.instance != null)
            GameManager.instance.ShowMouse(false);

        InspectionLightService.SetActive(false);
        moved = false;

        yield return StartCoroutine(FadeManager.instance.FadeOut());
    }

    #endregion

    #region Camera Effects

    public void Shake(float duration, float strength, int vibrato = 10, float randomness = 90f)
    {
        transform.DOShakePosition(duration, strength, vibrato, randomness, false, true);
    }

    public void WakeUpAnimation(float duration = 10f)
    {
        if (isMoving) return;
        isMoving = true;

        if (PlayerManager.instance != null)
            PlayerManager.instance.DesactiveInteractableRay();

        Sequence seq = DOTween.Sequence();

        seq.Append(transform.DORotate(new Vector3(20f, 0f, 0f), duration * 0.3f)
            .SetEase(Ease.InOutSine));

        seq.Append(transform.DORotate(Vector3.zero, duration * 0.5f)
            .SetEase(Ease.OutQuad));

        seq.OnComplete(() =>
        {
            if (PlayerManager.instance != null)
                PlayerManager.instance.ReactiveInteractableRay();
            isMoving = false;
        });
    }

    #endregion

    public void ResetScript()
    {
        isMoving = false;
        moved = false;
    }
}
