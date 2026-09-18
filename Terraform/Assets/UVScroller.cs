using UnityEngine;

namespace Terraform.Assets;

public class UVScroller : MonoBehaviour {
    public int TargetMaterialSlot;
    public float SpeedX;
    public float SpeedY = 0.5f;

    private Material _material;
    private float _accX;
    private float _accY;

    private void Start() {
        var renderer = GetComponent<Renderer>();
        if (renderer == null) return;
        var mats = renderer.materials;
        if (mats == null || TargetMaterialSlot < 0 || TargetMaterialSlot >= mats.Length) return;
        _material = mats[TargetMaterialSlot];
    }

    private void Update() {
        if (_material == null) return;
        _accX += Time.deltaTime * SpeedX;
        _accY += Time.deltaTime * SpeedY;
        _material.SetTextureOffset("_MainTex", new Vector2(_accX, _accY));
    }
}
