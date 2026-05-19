using UnityEngine;

namespace Terraform.Patches;

public struct DigRecord {
    public Vector3 WorldPos;
    public Vector3 Normal;
    public float   Radius;
    public DigMode Mode;
}
