using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using Unity.XR.XREAL;
using XREALCameraType = Unity.XR.XREAL.CameraType;

public class XREALCaptureManager : MonoBehaviour
{
    public static event Action<string, bool> OnCaptureStateChanged;

    [Header("Camera Coordination")]
    [SerializeField] private QRScanner qrScanner;

    [Tooltip("WebCamTexture를 파괴한 후 XREAL 카메라를 열기 전 대기 시간")]
    [SerializeField] private float qrCameraReleaseDelay = 0.25f;

    [Header("Capture Settings")]
    [SerializeField] private BlendMode blendMode = BlendMode.Blend;
    [SerializeField] private CaptureSide captureSide = CaptureSide.Single;

    [Tooltip("소리가 필요 없으면 None이 가장 안전합니다.")]
    [SerializeField] private AudioState audioState = AudioState.None;

    [SerializeField] private Color backgroundColor = Color.black;

    private XREALVideoCapture videoCapture;
    private XREALPhotoCapture photoCapture;

    private bool isTransitioning;
    private bool isTakingPhoto;

    private string currentVideoPath;
    private string currentVideoFileName;

    public bool IsRecording =>
        videoCapture != null && videoCapture.IsRecording;

    public bool IsBusy =>
        isTransitioning || isTakingPhoto || IsRecording;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Start()
    {
        PublishStatus("Capture ready", false);
    }

    private void ResolveReferences()
    {
        if (qrScanner == null)
            qrScanner = FindFirstObjectByType<QRScanner>();
    }

    // Beam Pro의 녹화 버튼이 호출한다.
    // 녹화 중이 아니면 시작하고, 녹화 중이면 정지한다.
    public void ToggleVideoRecording()
    {
        if (isTransitioning || isTakingPhoto)
        {
            PublishStatus("Camera is busy", IsRecording);
            return;
        }

        if (IsRecording)
        {
            StopVideoRecording();
            return;
        }

        StartCoroutine(ReleaseQrCameraThenStartVideo());
    }

    // Beam Pro의 JPEG Photo 버튼이 호출한다.
    public void TakeJpegPhoto()
    {
        if (IsRecording)
        {
            PublishStatus("Stop recording before taking a photo", true);
            return;
        }

        if (isTransitioning || isTakingPhoto)
        {
            PublishStatus("Camera is busy", false);
            return;
        }

        StartCoroutine(ReleaseQrCameraThenTakePhoto());
    }

    private IEnumerator ReleaseQrCameraThenStartVideo()
    {
        isTransitioning = true;
        PublishStatus("Releasing QR camera...", false);

        ResolveReferences();

        if (qrScanner != null)
            qrScanner.StopScanning();

        // QRScanner의 Destroy(camTexture)는 프레임 끝에 처리된다.
        yield return null;
        yield return new WaitForEndOfFrame();
        yield return new WaitForSeconds(qrCameraReleaseDelay);

        CreateAndStartVideoCapture();
    }

    private IEnumerator ReleaseQrCameraThenTakePhoto()
    {
        isTransitioning = true;
        isTakingPhoto = true;
        PublishStatus("Preparing JPEG camera...", false);

        ResolveReferences();

        if (qrScanner != null)
            qrScanner.StopScanning();

        yield return null;
        yield return new WaitForEndOfFrame();
        yield return new WaitForSeconds(qrCameraReleaseDelay);

        CreateAndStartPhotoCapture();
    }

    private void CreateAndStartVideoCapture()
    {
        currentVideoFileName =
            $"RadVis_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";

        currentVideoPath = Path.Combine(
            Application.persistentDataPath,
            currentVideoFileName
        );

        XREALVideoCaptureUtility.CreateAsync(
            false,
            capture =>
            {
                if (capture == null)
                {
                    FailVideo("Failed to create video capture");
                    return;
                }

                videoCapture = capture;

                Resolution resolution =
                    XREALVideoCaptureUtility
                        .SupportedResolutions
                        .First();

                CameraParameters parameters =
                    new CameraParameters();

                parameters.cameraType = XREALCameraType.RGB;
                parameters.cameraResolutionWidth = resolution.width;
                parameters.cameraResolutionHeight = resolution.height;
                parameters.frameRate = NativeConstants.RECORD_FPS_DEFAULT;

                // 비디오 인코딩 결과는 MP4다.
                // 이 값은 중간 프레임 처리 형식이다.
                parameters.pixelFormat = CapturePixelFormat.PNG;

                // 실제 카메라 영상 + Unity AR 오브젝트
                parameters.blendMode = blendMode;
                parameters.captureSide = captureSide;
                parameters.audioState = audioState;
                parameters.backgroundColor = backgroundColor;
                parameters.hologramOpacity = 1f;

                PublishStatus("Starting video mode...", false);

                videoCapture.StartVideoModeAsync(
                    parameters,
                    OnVideoModeStarted,
                    true
                );
            }
        );
    }

    private void OnVideoModeStarted(
        XREALVideoCapture.VideoCaptureResult result)
    {
        if (!result.success || videoCapture == null)
        {
            FailVideo("Failed to start video mode");
            return;
        }

        videoCapture.StartRecordingAsync(
            currentVideoPath,
            OnVideoRecordingStarted
        );
    }

    private void OnVideoRecordingStarted(
        XREALVideoCapture.VideoCaptureResult result)
    {
        isTransitioning = false;

        if (!result.success)
        {
            FailVideo("Failed to start recording");
            return;
        }

        PublishStatus("Recording...", true);
    }

    private void StopVideoRecording()
    {
        if (videoCapture == null || !videoCapture.IsRecording)
        {
            PublishStatus("Video is not recording", false);
            return;
        }

        isTransitioning = true;
        PublishStatus("Stopping recording...", true);

        videoCapture.StopRecordingAsync(
            OnVideoRecordingStopped
        );
    }

    private void OnVideoRecordingStopped(
        XREALVideoCapture.VideoCaptureResult result)
    {
        if (!result.success || videoCapture == null)
        {
            FailVideo("Failed to stop recording");
            return;
        }

        videoCapture.StopVideoModeAsync(
            OnVideoModeStopped
        );
    }

    private void OnVideoModeStopped(
        XREALVideoCapture.VideoCaptureResult result)
    {
        videoCapture?.Dispose();
        videoCapture = null;
        isTransitioning = false;

        if (!result.success)
        {
            PublishStatus("Failed to close video mode", false);
            return;
        }

        StartCoroutine(SaveVideoToGallery());
    }

    private IEnumerator SaveVideoToGallery()
    {
        // 인코더가 파일을 완전히 닫을 시간을 준다.
        yield return new WaitForSeconds(0.2f);

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            NativeGalleryDataProvider gallery =
                new NativeGalleryDataProvider();

            gallery.InsertVideo(
                currentVideoPath,
                currentVideoFileName,
                "Record"
            );
        }
        catch (Exception e)
        {
            Debug.LogError(
                $"[XREALCaptureManager] Video gallery insertion failed: {e}"
            );

            PublishStatus(
                $"MP4 saved in app folder: {currentVideoFileName}",
                false
            );

            yield break;
        }
#endif

        PublishStatus(
            $"MP4 saved: {currentVideoFileName}",
            false
        );
    }

    private void FailVideo(string message)
    {
        videoCapture?.Dispose();
        videoCapture = null;

        isTransitioning = false;

        PublishStatus(message, false);
        Debug.LogError($"[XREALCaptureManager] {message}");
    }

    private void CreateAndStartPhotoCapture()
    {
        XREALPhotoCapture.CreateAsync(
            false,
            capture =>
            {
                if (capture == null)
                {
                    FinishPhotoWithError(
                        "Failed to create photo capture"
                    );
                    return;
                }

                photoCapture = capture;

                Resolution resolution =
                    XREALPhotoCapture
                        .SupportedResolutions
                        .First();

                CameraParameters parameters =
                    new CameraParameters();

                parameters.cameraType = XREALCameraType.RGB;
                parameters.cameraResolutionWidth = resolution.width;
                parameters.cameraResolutionHeight = resolution.height;
                parameters.frameRate = NativeConstants.RECORD_FPS_DEFAULT;

                // JPEG 인코딩을 명시한다.
                parameters.pixelFormat = CapturePixelFormat.JPEG;

                parameters.blendMode = blendMode;
                parameters.captureSide = captureSide;
                parameters.audioState = AudioState.None;
                parameters.backgroundColor = backgroundColor;
                parameters.hologramOpacity = 1f;

                photoCapture.StartPhotoModeAsync(
                    parameters,
                    OnPhotoModeStarted,
                    true
                );
            }
        );
    }

    private void OnPhotoModeStarted(
        XREALPhotoCapture.PhotoCaptureResult result)
    {
        isTransitioning = false;

        if (!result.success || photoCapture == null)
        {
            FinishPhotoWithError(
                "Failed to start photo mode"
            );
            return;
        }

        PublishStatus("Taking JPEG photo...", false);

        photoCapture.TakePhotoAsync(
            OnPhotoCaptured
        );
    }

    private void OnPhotoCaptured(
        XREALPhotoCapture.PhotoCaptureResult result,
        PhotoCaptureFrame frame)
    {
        if (!result.success ||
            frame == null ||
            frame.TextureData == null ||
            frame.TextureData.Length == 0)
        {
            ClosePhotoCapture(
                "JPEG capture failed"
            );
            return;
        }

        try
        {
            string fileName =
                $"RadVis_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";

            string directory = Path.Combine(
                Application.persistentDataPath,
                "XrealShots"
            );

            Directory.CreateDirectory(directory);

            string filePath = Path.Combine(
                directory,
                fileName
            );

            byte[] jpegData = frame.TextureData;

            File.WriteAllBytes(
                filePath,
                jpegData
            );

            InsertJpegIntoGallery(
                jpegData,
                fileName
            );

            ClosePhotoCapture(
                $"JPEG saved: {fileName}"
            );
        }
        catch (Exception e)
        {
            Debug.LogError(
                $"[XREALCaptureManager] JPEG save failed: {e}"
            );

            ClosePhotoCapture(
                "JPEG save failed"
            );
        }
    }

    private void InsertJpegIntoGallery(
        byte[] jpegData,
        string fileName)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (AndroidJavaClass unityPlayer =
               new AndroidJavaClass(
                   "com.unity3d.player.UnityPlayer"))
        {
            using (AndroidJavaObject activity =
                   unityPlayer.GetStatic<AndroidJavaObject>(
                       "currentActivity"))
            {
                using (AndroidJavaObject provider =
                       new AndroidJavaObject(
                           "ai.nreal.android.gallery.GalleryDataProvider",
                           activity))
                {
                    using (AndroidJavaObject inputStream =
                           new AndroidJavaObject(
                               "java.io.ByteArrayInputStream",
                               jpegData))
                    {
                        AndroidJavaObject result =
                            provider.Call<AndroidJavaObject>(
                                "insertImage",
                                inputStream,
                                fileName,
                                "Screenshots",
                                "image/jpeg"
                            );

                        result?.Dispose();
                    }
                }
            }
        }
#endif
    }

    private void ClosePhotoCapture(
        string finalMessage)
    {
        if (photoCapture == null)
        {
            isTakingPhoto = false;
            isTransitioning = false;
            PublishStatus(finalMessage, false);
            return;
        }

        photoCapture.StopPhotoModeAsync(
            result =>
            {
                photoCapture?.Dispose();
                photoCapture = null;

                isTakingPhoto = false;
                isTransitioning = false;

                PublishStatus(finalMessage, false);
            }
        );
    }

    private void FinishPhotoWithError(string message)
    {
        photoCapture?.Dispose();
        photoCapture = null;

        isTakingPhoto = false;
        isTransitioning = false;

        PublishStatus(message, false);
        Debug.LogError($"[XREALCaptureManager] {message}");
    }

    private void PublishStatus(
        string message,
        bool recording)
    {
        Debug.Log(
            $"[XREALCaptureManager] {message}"
        );

        OnCaptureStateChanged?.Invoke(
            message,
            recording
        );
    }

    private void OnDestroy()
    {
        photoCapture?.Dispose();
        photoCapture = null;

        videoCapture?.Dispose();
        videoCapture = null;
    }
}
