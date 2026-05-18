using UnityEngine;

namespace Terraform.Assets;

public class UVScroller : MonoBehaviour {
    public int TargetMaterialSlot;
    public float SpeedX;
    public float SpeedY = 0.5f;

    private Renderer _renderer;
    private float _accX;
    private float _accY;

    private void Start() {
        _renderer = GetComponent<Renderer>();
    }

    private void Update() {
        if (_renderer == null) return;
        _accX += Time.deltaTime * SpeedX;
        _accY += Time.deltaTime * SpeedY;
        var mats = _renderer.materials;
        if (mats == null || TargetMaterialSlot < 0 || TargetMaterialSlot >= mats.Length) return;
        mats[TargetMaterialSlot].SetTextureOffset("_MainTex", new Vector2(_accX, _accY));
    }
}
