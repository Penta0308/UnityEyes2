using UnityEngine;
using SimpleJSON;

/// <summary>
/// Generates identity-consistent groups in the order
/// identity -> pose -> pupil diameter. It owns scheduling only; rendering and
/// serialization remain in SynthesEyesServer and anatomical changes remain in
/// their respective controllers.
/// </summary>
public class GroupedRandomizationController : MonoBehaviour
{
    private SynthesEyesServer server;
    private EyeRegionController eyeRegion;
    private EyeballController eyeball;

    private int posesPerIdentity = 1;
    private float[] pupilDiametersMm = { 4.0f };
    private int baseSeed = 1729;

    private int identityIndex;
    private int poseIndex;
    private int pupilIndex;
    private bool identityPrepared;
    private bool posePrepared;
    private bool samplePrepared;

    public int IdentityIndex => identityIndex;
    public int PoseIndex => poseIndex;
    public int PupilIndex => pupilIndex;
    public float PupilDiameterMm => pupilDiametersMm[pupilIndex];

    public void Configure(
        SynthesEyesServer owner,
        EyeRegionController region,
        EyeballController ball,
        int poseCount,
        float[] pupilSizesMm,
        int seed)
    {
        server = owner;
        eyeRegion = region;
        eyeball = ball;
        posesPerIdentity = Mathf.Max(1, poseCount);
        pupilDiametersMm = pupilSizesMm != null && pupilSizesMm.Length > 0
            ? pupilSizesMm
            : new[] { 4.0f };
        baseSeed = seed;

        // Appearance is now changed explicitly at identity boundaries rather
        // than implicitly on every EyeRegionController.UpdateEyeRegion call.
        eyeRegion.randomizeAppearance = false;
        ResetSequence();
    }

    public void ResetSequence()
    {
        identityIndex = 0;
        poseIndex = 0;
        pupilIndex = 0;
        identityPrepared = false;
        posePrepared = false;
        samplePrepared = false;
    }

    public void PrepareCurrentSample()
    {
        if (samplePrepared) return;

        if (!identityPrepared)
        {
            Random.InitState(baseSeed + identityIndex);
            eyeRegion.RandomizeAppearance();
            eyeball.RandomizeIdentity();
            server.RandomizeIdentityLashes();
            identityPrepared = true;
        }

        if (!posePrepared)
        {
            server.RandomizePose();
            posePrepared = true;
        }

        eyeball.SetPupilDiameterMm(PupilDiameterMm);
        samplePrepared = true;
    }

    public void AdvanceAfterSave()
    {
        samplePrepared = false;
        pupilIndex++;

        if (pupilIndex < pupilDiametersMm.Length) return;

        pupilIndex = 0;
        poseIndex++;
        posePrepared = false;

        if (poseIndex < posesPerIdentity) return;

        poseIndex = 0;
        identityIndex++;
        identityPrepared = false;
    }

    public JSONNode GetMetadata()
    {
        JSONNode node = new JSONClass();
        node.Add("identity_index", new JSONData(identityIndex));
        node.Add("pose_index", new JSONData(poseIndex));
        node.Add("pupil_index", new JSONData(pupilIndex));
        node.Add("pupil_diameter_mm", new JSONData(PupilDiameterMm));
        node.Add("seed", new JSONData(baseSeed + identityIndex));
        return node;
    }
}
