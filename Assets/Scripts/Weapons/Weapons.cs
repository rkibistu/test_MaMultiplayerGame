﻿using System.Collections.Generic;
using Fusion;
using Unity.VisualScripting;
using UnityEngine;


public class WeaponContext {
    public IProjectileManager ProjectileManager;

    public Vector3 FireDirection;
    public Vector3 FirePosition;

    public NetworkButtons Input;
    public NetworkButtons PressedInput;
    public NetworkButtons ReleasedInput;

    public int OwnerObjectInstanceID;
}

[RequireComponent(typeof(ProjectileManager))]
public class Weapons : NetworkBehaviour {
    // PUBLIC MEMBERS

    public bool IsSwitchingWeapon => _switchCooldown.ExpiredOrNotRunning(Runner) == false;
    public float ElapsedSwitchTime => _weaponSwitchDuration - _switchCooldown.RemainingTime(Runner).GetValueOrDefault();

    public Weapon CurrentWeapon => _weapons[CurrentWeaponSlot];
    public Weapon PendingWeapon => _weapons[PendingWeaponSlot];

    [Networked, HideInInspector]
    public int CurrentWeaponSlot { get; private set; }
    [Networked(OnChanged = nameof(OnPendingWeaponChanged), OnChangedTargets = OnChangedTargets.All), HideInInspector]
    public int PendingWeaponSlot { get; private set; }

    public int Version => _version;

    // PRIVATE MEMBERS

    [Networked(OnChanged = nameof(OnWeaponsChanged), OnChangedTargets = OnChangedTargets.All), Capacity(12)]
    private NetworkArray<Weapon> _weapons { get; }
    [Networked]
    private TickTimer _switchCooldown { get; set; }

    [SerializeField]
    private Weapon[] _initialWeapons;
    [SerializeField]
    private Transform _weaponsRoot;
    [SerializeField]
    private WeaponHolder _weaponHolder;
    [SerializeField]
    private Transform _fireTransform;
    [SerializeField]
    private Vector3 _firstPersonWeaponOffset = new Vector3(-0.15f, 0f, 0f);

    [Header("Weapon Switch")]
    [SerializeField]
    private float _weaponSwitchDuration = 1f;
    [SerializeField, Tooltip("When the actual weapon swap happens during weapon switch")]
    private float _weaponSwapTime = 0.5f;

    private int _version;

    private ProjectileManager _projectileManager;
    private WeaponContext _context = new WeaponContext();

    private bool _forceWeaponsRefresh;

    // PUBLIC METHODS

    public void ProcessInput(PlayerInput input) {
        if (IsProxy == true)
            return;

        if (CurrentWeapon == null)
            return;

        SwitchWeapon(input.FixedInput.WeaponSlot, false);

        if (IsSwitchingWeapon == false) {
            _context.Input = input.FixedInput.Buttons;

            //Debug.Log(input.IsSet(EInputButtons.Fire) + "   " + input.IsSet(EInputButtons.ZoomWeapon));

            _context.PressedInput = input.GetPressedButtons();
            _context.ReleasedInput = input.GetReleasedButtons();
            _context.FirePosition = _fireTransform.position;
            _context.FireDirection = _fireTransform.rotation * Vector3.forward;

            CurrentWeapon.ProcessInput(_context);
        }
    }

    public void SwitchWeapon(int weaponSlot, bool immediate) {
        if (weaponSlot < 0 || weaponSlot >= _weapons.Length)
            return;

        var weapon = _weapons[weaponSlot];
        if (weapon == null)
            return;

        if (immediate == true || _weaponSwitchDuration <= 0f) {
            PendingWeaponSlot = weaponSlot;
            CurrentWeaponSlot = weaponSlot;
            _switchCooldown = default;
        }
        else {
            StartWeaponSwitch(weaponSlot);
        }
    }

    public void OnSpawned() {
        _projectileManager.OnSpawned();

        if (HasStateAuthority == false)
            return;

        int minWeaponSlot = 0;

        // Spawn initial weapons
        for (int i = 0; i < _initialWeapons.Length; i++) {
            var weaponPrefab = _initialWeapons[i];
            if (weaponPrefab == null)
                continue;

            var weapon = Runner.Spawn(weaponPrefab, inputAuthority: Object.InputAuthority);
            AddWeapon(weapon);

            if (minWeaponSlot == 0 || minWeaponSlot > weapon.WeaponSlot) {
                minWeaponSlot = weapon.WeaponSlot;
            }
        }

        // Equip first weapon
        SwitchWeapon(minWeaponSlot, true);
    }

    public void OnLateFixedUpdate() {
        UpdateWeaponSwitch();

        _projectileManager.OnFixedUpdate();
    }

    public void OnRender() {
        if (CurrentWeapon == null)
            return;

        //bool isFirstPerson = Context.ObservedPlayerRef == Object.InputAuthority;
        bool isFirstPerson = true;
        int layer = isFirstPerson == true ? ObjectLayer.FirstPerson : ObjectLayer.ThirdPerson;

        _context.FirePosition = _fireTransform.position;
        _context.FireDirection = _fireTransform.rotation * Vector3.forward;

        RefreshWeapons();
        SetWeaponView(layer, isFirstPerson == true ? _firstPersonWeaponOffset : Vector3.zero);

        _projectileManager.OnRender(CurrentWeapon.BarrelTransforms);
        CurrentWeapon.OnRender(_context);
    }

    public void OnDespawned() {
        // Cleanup weapons
        for (int i = 0; i < _weapons.Length; i++) {
            var weapon = _weapons[i];
            if (weapon != null) {
                RemoveWeapon(weapon.WeaponSlot, true);
            }
        }
    }

    public int GetNextWeaponSlot(int fromSlot, bool ignoreZeroWeapon = false) {
        int weaponsLength = _weapons.Length;

        for (int i = 0; i < weaponsLength; i++) {
            int slot = (fromSlot + i + 1) % weaponsLength;

            if (slot == 0 && ignoreZeroWeapon == true)
                continue;

            if (_weapons[slot] != null)
                return slot;
        }

        return 0;
    }

    public int GetPreviousWeaponSlot(int fromSlot, bool ignoreZeroWeapon = false) {
        int weaponsLength = _weapons.Length;

        for (int i = 0; i < weaponsLength; i++) {
            int slot = (weaponsLength + fromSlot - i - 1) % weaponsLength;

            if (slot == 0 && ignoreZeroWeapon == true)
                continue;

            if (_weapons[slot] != null)
                return slot;
        }

        return 0;
    }

    public void GetAllWeapons(List<Weapon> weapons) {
        for (int i = 0; i < _weapons.Length; i++) {
            if (_weapons[i] != null) {
                weapons.Add(_weapons[i]);
            }
        }
    }


    // Method called by players to add a new weapon to their inventory
    //          called when pickup a weapon
    //weaponPrefabIdentifier - is q uniqeu identifier for weapon prefabs
    [Rpc(sources: RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    public void AddWeapon_RPC(int weaponPrefabIdentifier, RpcInfo info = default) {

        if (HasStateAuthority == false)
            return;

        var weapon = Runner.Spawn(ResourceManager.Instance.weaponPrefabs[weaponPrefabIdentifier], inputAuthority: Object.InputAuthority);

        AgentStateMachine agent;
        if (info.Source.IsNone)
            agent = RoomPlayer.LocalRoomPlayer.ActiveAgent;
        else
            agent = Context.Instance.Gameplay.Players[info.Source].ActiveAgent;

        agent.Weapons.AddWeapon(weapon.GetComponent<Weapon>());
    }

   
    // MONOBEHAVIOUR

    protected void Awake() {
        _projectileManager = GetComponent<ProjectileManager>();



        _context.ProjectileManager = _projectileManager;
        _context.OwnerObjectInstanceID = gameObject.GetInstanceID();
    }

    // PRIVATE METHODS

    private void StartWeaponSwitch(int weaponSlot) {
        if (weaponSlot == PendingWeaponSlot)
            return;

        PendingWeaponSlot = weaponSlot;

        if (ElapsedSwitchTime < _weaponSwapTime)
            return; // We haven't swap weapon yet, just continue with new pending weapon

        _switchCooldown = TickTimer.CreateFromSeconds(Runner, _weaponSwitchDuration);
    }

    private void UpdateWeaponSwitch() {
        if (IsProxy == true)
            return;

        if (CurrentWeaponSlot == PendingWeaponSlot)
            return;

        if (ElapsedSwitchTime < _weaponSwapTime)
            return;

        CurrentWeaponSlot = PendingWeaponSlot;
    }

    private void RefreshWeapons() {
        var currentWeapon = CurrentWeapon;

        if (_forceWeaponsRefresh == false && currentWeapon.IsArmed == true)
            return; // Proper weapon is ready

        for (int i = 0; i < _weapons.Length; i++) {
            var weapon = _weapons[i];

            if (weapon == null)
                continue;

            if (weapon != currentWeapon) {
                weapon.DisarmWeapon();
                weapon.SetActive(false);
            }
        }

        currentWeapon.transform.SetParent(_weaponsRoot, false);
        currentWeapon.SetActive(true);

        currentWeapon.ArmWeapon();

        _forceWeaponsRefresh = false;
    }

    private void SetWeaponView(int layer, Vector3 offset) {
        var currentWeapon = CurrentWeapon;

        if (currentWeapon == null)
            return;

        if (currentWeapon.gameObject.layer != layer) {
            // First person weapon is rendered differently (see ForwardRenderer asset)
            currentWeapon.gameObject.SetLayer(layer, true);
        }

        // Weapon is in different position for first person vs third person to align nicely in camera view
        // HERE you can set the position and rotation of the weapon
        //currentWeapon.transform.localPosition = offset;
        //currentWeapon.transform.localRotation = Quaternion.Euler(X, Y, Z);

        _weaponHolder.SetInitialHandsPosition();
    }
    private void AddWeapon(Weapon weapon) {
        if (weapon == null)
            return;

        if (_weapons[weapon.WeaponSlot]) {

            DropWeapon(weapon.WeaponSlot, transform);
        }
        RemoveWeapon(weapon.WeaponSlot);


        weapon.Object.AssignInputAuthority(Object.InputAuthority);

        _weapons.Set(weapon.WeaponSlot, weapon);
    }

    private void RemoveWeapon(int slot, bool despawn = true) {
        var weapon = _weapons[slot];
        if (weapon == null)
            return;

        if (despawn == true) {
            Runner.Despawn(weapon.Object);
        }
        else {
            weapon.Object.RemoveInputAuthority();
        }

        _weapons.Set(slot, null);
    }

    private void DropWeapon(int weaponSlot, Transform source) {

        GameObject toDrop = _weapons[weaponSlot].PickableWeaponPrefab;
        if (toDrop) {

            Debug.LogWarning("Drop weapon: " + toDrop + ". Pos: " + source.position);
            var obj = Runner.Spawn(toDrop);
            obj.transform.position = source.position;
        }
    }
    // NETWORK CALLBACKS

    public static void OnPendingWeaponChanged(Changed<Weapons> changed) {
        //changed.Behaviour._agent.Effects.PlaySound(changed.Behaviour._weaponSwitchSound);
    }

    public static void OnWeaponsChanged(Changed<Weapons> changed) {
        changed.Behaviour._forceWeaponsRefresh = true;
        changed.Behaviour._version++;
    }
}
