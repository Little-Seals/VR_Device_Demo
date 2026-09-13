using UnityEngine;
using System;

public class InteractWithVirtual : SerialController
{
    [Header("无碰撞平滑跟随")]
    [Tooltip("无碰撞时 Link 跟随电机角度的平滑时间，越小跟随越快")]
    [SerializeField] private float followSmoothTime = 0.03f;

    [Tooltip("无碰撞时 Link 的最大跟随速度，单位：度/秒")]
    [SerializeField] private float maxFollowSpeed = 720f;

    [Header("碰撞力反馈")]
    [Tooltip("位置差控制增益，与 ESP32 原 main.cpp 中 4*(target-cur) 的增益一致")]
    [SerializeField] private float Kp = 4f;

    private float _lastSentForce = 0f;
    private const float ForceThreshold = 0.005f;
    private bool isSend = false;

    private float theta1 = 0f;
    private float theta2 = 0f;
    private float _force = 0f;

    // SmoothDampAngle 需要的速度缓存，单位：度/秒
    private float followYVelocity = 0f;

    public Rigidbody rigidBody;

    protected override void Start()
    {
        base.Start();
        rigidBody = GetComponent<Rigidbody>();

        // 让物理结果在渲染帧之间平滑显示，降低视觉抖动
        rigidBody.interpolation = RigidbodyInterpolation.Interpolate;

        // 设置物理更新频率
        Time.fixedDeltaTime = 1f / 250f;
    }

    protected override void Update()
    {
        WriteToCache(_force);
    }

    private void FixedUpdate()
    {
        // 串口目标角度和 Link 当前角度都按物理节拍读取，避免使用过期角度
        ReadFromCache();
        theta2 = WrapAngle180(rigidBody.rotation.eulerAngles.y);

        if (!isColliding)
        {
            // 自由空间只做视觉跟随，不使用力矩控制，避免 P 控制产生振荡
            FollowMotorVisually();
            _force = 0f;
            return;
        }

        // 碰撞时才进入力反馈控制
        MotionUpdate();
    }

    /// <summary>
    /// 无碰撞时直接平滑跟随电机角度。
    /// 使用 MoveRotation 而不是 AddTorque，因此不会形成“弹簧-惯量”振荡。
    /// </summary>
    private void FollowMotorVisually()
    {
        float currentY = rigidBody.rotation.eulerAngles.y;

        float nextY = Mathf.SmoothDampAngle(
            currentY,
            theta1,
            ref followYVelocity,
            followSmoothTime,
            maxFollowSpeed,
            Time.fixedDeltaTime
        );

        rigidBody.MoveRotation(Quaternion.Euler(0f, nextY, 0f));
    }

    /// <summary>
    /// 碰撞时保留原来的位置差力矩控制，用于计算并反馈虚拟环境作用力。
    /// </summary>
    private void MotionUpdate()
    {
        // 计算角度差（最短路径）
        float angleDiff = WrapAngle180(theta1 - theta2);

        // 计算 Link 跟随力矩
        float torque2 = Kp * angleDiff;

        // 应用力矩（绕 Y 轴）
        rigidBody.AddTorque(Vector3.up * torque2, ForceMode.Acceleration);

        // 计算发送给电机的反向力矩
        float errorDeg = WrapAngle180(theta2 - theta1);
        _force = Kp * errorDeg * Mathf.Deg2Rad;

        // 小力矩死区，避免浮点噪声导致串口持续发送
        if (Mathf.Abs(_force) < 0.005f)
        {
            _force = 0f;
        }
    }

    private void ReadFromCache()
    {
        if (!NewDataReceived)
        {
            return;
        }

        lock (DataLock)
        {
            CurrentAngle = ThreadSafeAngle;
            ThreadSafeDataFlag = false;
        }
        NewDataReceived = false;

        theta1 = WrapAngle180(CurrentAngle);
    }

    private void WriteToCache(float force)
    {
        // 如果力矩变化超过阈值，发送新力矩
        if (Mathf.Abs(force - _lastSentForce) > ForceThreshold)
        {
            // 根据电机控制程序的要求，格式为 "F" + 力矩值，例如 "F1.570"
            SendData("F" + force.ToString("F3"));
            _lastSentForce = force;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        // 将平滑跟随速度交给物理控制，减少模式切换时的突变
        followYVelocity = rigidBody.angularVelocity.y * Mathf.Rad2Deg;
        isColliding = true;
    }

    private void OnCollisionExit(Collision collision)
    {
        _force = 0f;
        WriteToCache(_force);

        // 退出碰撞后恢复平滑跟随，并把当前物理角速度作为初始跟随速度
        followYVelocity = rigidBody.angularVelocity.y * Mathf.Rad2Deg;
        isColliding = false;
    }

    /// <summary>
    /// 将角度差值包装到 [-180, 180] 范围。
    /// </summary>
    private float WrapAngle180(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
        {
            angle -= 360f;
        }
        else if (angle <= -180f)
        {
            angle += 360f;
        }
        return angle;
    }

    protected override void ProcessReceivedData(string data)
    {
        try
        {
            if (float.TryParse(data, out float angle))
            {
                lock (DataLock)
                {
                    ThreadSafeAngle = angle;
                    ThreadSafeDataFlag = true;
                }
                NewDataReceived = true;
            }
        }
        catch (FormatException ex)
        {
            Debug.LogWarning($"数据格式错误: {data}, 错误: {ex.Message}");
        }
    }
}
