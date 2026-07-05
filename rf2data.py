"""
rFactor 2 / Le Mans Ultimate 공유 메모리 구조체 매핑.

The Iron Wolf의 rF2 Shared Memory Map Plugin(rFactor2SharedMemoryMapPlugin64.dll)이
쓰는 공유 메모리 레이아웃을 ctypes로 매핑한 것.

필드명/타입/순서는 pyRfactor2SharedMemory 프로젝트의 rF2data.py
(https://github.com/TonyWhitley/pyRfactor2SharedMemory, rF2data.cs에서 자동 생성)를
그대로 가져왔다. 구조체 크기가 1바이트라도 어긋나면 모든 필드가 깨지므로
임의로 필드를 추가/삭제/재정렬하지 말 것.

원 저작자:
  - 플러그인/레이아웃: The Iron Wolf (vleonavicius@hotmail.com, thecrewchief.org)
  - 파이썬 매핑: Tony Whitley (pyRfactor2SharedMemory)
"""
# pylint: disable=C,R,W

import ctypes


class rFactor2Constants:
    MAX_MAPPED_VEHICLES = 128
    MAX_MAPPED_IDS = 512
    MAX_RULES_INSTRUCTION_MSG_LEN = 96
    MAX_STATUS_MSG_LEN = 128
    MAX_HWCONTROL_NAME_LEN = 96

    MM_TELEMETRY_FILE_NAME = "$rFactor2SMMP_Telemetry$"
    MM_SCORING_FILE_NAME = "$rFactor2SMMP_Scoring$"
    MM_EXTENDED_FILE_NAME = "$rFactor2SMMP_Extended$"


class rF2GamePhase:
    Garage = 0
    WarmUp = 1
    GridWalk = 2
    Formation = 3
    Countdown = 4
    GreenFlag = 5
    FullCourseYellow = 6
    SessionStopped = 7
    SessionOver = 8
    PausedOrHeartbeat = 9


class rF2PitState:
    NONE = 0
    Request = 1
    Entering = 2
    Stopped = 3
    Exiting = 4


class rF2FinishStatus:
    NONE = 0
    Finished = 1
    Dnf = 2
    Dq = 3


class rF2Vec3(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('x', ctypes.c_double),
        ('y', ctypes.c_double),
        ('z', ctypes.c_double),
    ]


class rF2Wheel(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mSuspensionDeflection', ctypes.c_double),         # meters
        ('mRideHeight', ctypes.c_double),                   # meters
        ('mSuspForce', ctypes.c_double),                    # pushrod load in Newtons
        ('mBrakeTemp', ctypes.c_double),                    # Celsius
        ('mBrakePressure', ctypes.c_double),                # 0.0-1.0
        ('mRotation', ctypes.c_double),                     # radians/sec
        ('mLateralPatchVel', ctypes.c_double),
        ('mLongitudinalPatchVel', ctypes.c_double),
        ('mLateralGroundVel', ctypes.c_double),
        ('mLongitudinalGroundVel', ctypes.c_double),
        ('mCamber', ctypes.c_double),                       # radians
        ('mLateralForce', ctypes.c_double),                 # Newtons
        ('mLongitudinalForce', ctypes.c_double),            # Newtons
        ('mTireLoad', ctypes.c_double),                     # Newtons
        ('mGripFract', ctypes.c_double),
        ('mPressure', ctypes.c_double),                     # kPa
        ('mTemperature', ctypes.c_double * 3),              # Kelvin, left/center/right
        ('mWear', ctypes.c_double),                         # 0.0-1.0 (남은 정도가 아니라 "최대치 대비 분율", 1.0=새 타이어)
        ('mTerrainName', ctypes.c_ubyte * 16),
        ('mSurfaceType', ctypes.c_ubyte),                   # 0=dry .. 6=special
        ('mFlat', ctypes.c_ubyte),
        ('mDetached', ctypes.c_ubyte),
        ('mStaticUndeflectedRadius', ctypes.c_ubyte),       # cm
        ('mVerticalTireDeflection', ctypes.c_double),
        ('mWheelYLocation', ctypes.c_double),
        ('mToe', ctypes.c_double),
        ('mTireCarcassTemperature', ctypes.c_double),       # Kelvin
        ('mTireInnerLayerTemperature', ctypes.c_double * 3),  # Kelvin
        ('mExpansion', ctypes.c_ubyte * 24),
    ]


class rF2VehicleTelemetry(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mID', ctypes.c_int),
        ('mDeltaTime', ctypes.c_double),
        ('mElapsedTime', ctypes.c_double),
        ('mLapNumber', ctypes.c_int),
        ('mLapStartET', ctypes.c_double),
        ('mVehicleName', ctypes.c_ubyte * 64),
        ('mTrackName', ctypes.c_ubyte * 64),
        ('mPos', rF2Vec3),
        ('mLocalVel', rF2Vec3),                             # m/s, 차량 로컬 좌표
        ('mLocalAccel', rF2Vec3),
        ('mOri', rF2Vec3 * 3),
        ('mLocalRot', rF2Vec3),
        ('mLocalRotAccel', rF2Vec3),
        ('mGear', ctypes.c_int),                            # -1=R, 0=N, 1+
        ('mEngineRPM', ctypes.c_double),
        ('mEngineWaterTemp', ctypes.c_double),              # Celsius
        ('mEngineOilTemp', ctypes.c_double),                # Celsius
        ('mClutchRPM', ctypes.c_double),
        ('mUnfilteredThrottle', ctypes.c_double),
        ('mUnfilteredBrake', ctypes.c_double),
        ('mUnfilteredSteering', ctypes.c_double),
        ('mUnfilteredClutch', ctypes.c_double),
        ('mFilteredThrottle', ctypes.c_double),
        ('mFilteredBrake', ctypes.c_double),
        ('mFilteredSteering', ctypes.c_double),
        ('mFilteredClutch', ctypes.c_double),
        ('mSteeringShaftTorque', ctypes.c_double),
        ('mFront3rdDeflection', ctypes.c_double),
        ('mRear3rdDeflection', ctypes.c_double),
        ('mFrontWingHeight', ctypes.c_double),
        ('mFrontRideHeight', ctypes.c_double),
        ('mRearRideHeight', ctypes.c_double),
        ('mDrag', ctypes.c_double),
        ('mFrontDownforce', ctypes.c_double),
        ('mRearDownforce', ctypes.c_double),
        ('mFuel', ctypes.c_double),                         # liters
        ('mEngineMaxRPM', ctypes.c_double),
        ('mScheduledStops', ctypes.c_ubyte),
        ('mOverheating', ctypes.c_ubyte),
        ('mDetached', ctypes.c_ubyte),
        ('mHeadlights', ctypes.c_ubyte),
        ('mDentSeverity', ctypes.c_ubyte * 8),              # 차체 8곳 데미지 0/1/2
        ('mLastImpactET', ctypes.c_double),
        ('mLastImpactMagnitude', ctypes.c_double),
        ('mLastImpactPos', rF2Vec3),
        ('mEngineTorque', ctypes.c_double),
        ('mCurrentSector', ctypes.c_int),                   # 0-based, sign bit = 피트레인
        ('mSpeedLimiter', ctypes.c_ubyte),
        ('mMaxGears', ctypes.c_ubyte),
        ('mFrontTireCompoundIndex', ctypes.c_ubyte),
        ('mRearTireCompoundIndex', ctypes.c_ubyte),
        ('mFuelCapacity', ctypes.c_double),                 # liters
        ('mFrontFlapActivated', ctypes.c_ubyte),
        ('mRearFlapActivated', ctypes.c_ubyte),
        ('mRearFlapLegalStatus', ctypes.c_ubyte),
        ('mIgnitionStarter', ctypes.c_ubyte),
        ('mFrontTireCompoundName', ctypes.c_ubyte * 18),
        ('mRearTireCompoundName', ctypes.c_ubyte * 18),
        ('mSpeedLimiterAvailable', ctypes.c_ubyte),
        ('mAntiStallActivated', ctypes.c_ubyte),
        ('mUnused', ctypes.c_ubyte * 2),
        ('mVisualSteeringWheelRange', ctypes.c_float),
        ('mRearBrakeBias', ctypes.c_double),
        ('mTurboBoostPressure', ctypes.c_double),
        ('mPhysicsToGraphicsOffset', ctypes.c_float * 3),
        ('mPhysicalSteeringWheelRange', ctypes.c_float),
        ('mExpansion', ctypes.c_ubyte * 152),
        ('mWheels', rF2Wheel * 4),                          # FL, FR, RL, RR
    ]


class rF2ScoringInfo(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mTrackName', ctypes.c_ubyte * 64),
        ('mSession', ctypes.c_int),                         # 0=test 1-4=practice 5-8=qual 9=warmup 10-13=race
        ('mCurrentET', ctypes.c_double),
        ('mEndET', ctypes.c_double),
        ('mMaxLaps', ctypes.c_int),
        ('mLapDist', ctypes.c_double),                      # 트랙 길이 (m)
        ('pointer1', ctypes.c_ubyte * 8),
        ('mNumVehicles', ctypes.c_int),
        ('mGamePhase', ctypes.c_ubyte),
        ('mYellowFlagState', ctypes.c_ubyte),
        ('mSectorFlag', ctypes.c_ubyte * 3),
        ('mStartLight', ctypes.c_ubyte),
        ('mNumRedLights', ctypes.c_ubyte),
        ('mInRealtime', ctypes.c_ubyte),
        ('mPlayerName', ctypes.c_ubyte * 32),
        ('mPlrFileName', ctypes.c_ubyte * 64),
        ('mDarkCloud', ctypes.c_double),                    # 0.0-1.0
        ('mRaining', ctypes.c_double),                      # 0.0-1.0
        ('mAmbientTemp', ctypes.c_double),                  # Celsius
        ('mTrackTemp', ctypes.c_double),                    # Celsius
        ('mWind', rF2Vec3),
        ('mMinPathWetness', ctypes.c_double),
        ('mMaxPathWetness', ctypes.c_double),
        ('mGameMode', ctypes.c_ubyte),
        ('mIsPasswordProtected', ctypes.c_ubyte),
        ('mServerPort', ctypes.c_short),
        ('mServerPublicIP', ctypes.c_int),
        ('mMaxPlayers', ctypes.c_int),
        ('mServerName', ctypes.c_ubyte * 32),
        ('mStartET', ctypes.c_float),
        ('mAvgPathWetness', ctypes.c_double),
        ('mExpansion', ctypes.c_ubyte * 200),
        ('pointer2', ctypes.c_ubyte * 8),
    ]


class rF2VehicleScoring(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mID', ctypes.c_int),
        ('mDriverName', ctypes.c_ubyte * 32),
        ('mVehicleName', ctypes.c_ubyte * 64),
        ('mTotalLaps', ctypes.c_short),                     # 완료한 랩 수
        ('mSector', ctypes.c_ubyte),                        # 0=sector3, 1=sector1, 2=sector2
        ('mFinishStatus', ctypes.c_ubyte),
        ('mLapDist', ctypes.c_double),                      # 현재 랩 진행 거리 (m)
        ('mPathLateral', ctypes.c_double),
        ('mTrackEdge', ctypes.c_double),
        ('mBestSector1', ctypes.c_double),
        ('mBestSector2', ctypes.c_double),                  # S1+S2 누적
        ('mBestLapTime', ctypes.c_double),
        ('mLastSector1', ctypes.c_double),
        ('mLastSector2', ctypes.c_double),                  # S1+S2 누적
        ('mLastLapTime', ctypes.c_double),
        ('mCurSector1', ctypes.c_double),
        ('mCurSector2', ctypes.c_double),
        ('mNumPitstops', ctypes.c_short),
        ('mNumPenalties', ctypes.c_short),
        ('mIsPlayer', ctypes.c_ubyte),
        ('mControl', ctypes.c_ubyte),
        ('mInPits', ctypes.c_ubyte),
        ('mPlace', ctypes.c_ubyte),                         # 1-based 순위
        ('mVehicleClass', ctypes.c_ubyte * 32),
        ('mTimeBehindNext', ctypes.c_double),
        ('mLapsBehindNext', ctypes.c_int),
        ('mTimeBehindLeader', ctypes.c_double),
        ('mLapsBehindLeader', ctypes.c_int),
        ('mLapStartET', ctypes.c_double),
        ('mPos', rF2Vec3),
        ('mLocalVel', rF2Vec3),
        ('mLocalAccel', rF2Vec3),
        ('mOri', rF2Vec3 * 3),
        ('mLocalRot', rF2Vec3),
        ('mLocalRotAccel', rF2Vec3),
        ('mHeadlights', ctypes.c_ubyte),
        ('mPitState', ctypes.c_ubyte),                      # 0=none 1=request 2=entering 3=stopped 4=exiting
        ('mServerScored', ctypes.c_ubyte),
        ('mIndividualPhase', ctypes.c_ubyte),
        ('mQualification', ctypes.c_int),
        ('mTimeIntoLap', ctypes.c_double),
        ('mEstimatedLapTime', ctypes.c_double),
        ('mPitGroup', ctypes.c_ubyte * 24),
        ('mFlag', ctypes.c_ubyte),                          # 0=green, 6=blue
        ('mUnderYellow', ctypes.c_ubyte),
        ('mCountLapFlag', ctypes.c_ubyte),
        ('mInGarageStall', ctypes.c_ubyte),
        ('mUpgradePack', ctypes.c_ubyte * 16),
        ('mPitLapDist', ctypes.c_float),
        ('mBestLapSector1', ctypes.c_float),
        ('mBestLapSector2', ctypes.c_float),
        ('mExpansion', ctypes.c_ubyte * 48),
    ]


class rF2PhysicsOptions(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mTractionControl', ctypes.c_ubyte),
        ('mAntiLockBrakes', ctypes.c_ubyte),
        ('mStabilityControl', ctypes.c_ubyte),
        ('mAutoShift', ctypes.c_ubyte),
        ('mAutoClutch', ctypes.c_ubyte),
        ('mInvulnerable', ctypes.c_ubyte),
        ('mOppositeLock', ctypes.c_ubyte),
        ('mSteeringHelp', ctypes.c_ubyte),
        ('mBrakingHelp', ctypes.c_ubyte),
        ('mSpinRecovery', ctypes.c_ubyte),
        ('mAutoPit', ctypes.c_ubyte),
        ('mAutoLift', ctypes.c_ubyte),
        ('mAutoBlip', ctypes.c_ubyte),
        ('mFuelMult', ctypes.c_ubyte),
        ('mTireMult', ctypes.c_ubyte),
        ('mMechFail', ctypes.c_ubyte),
        ('mAllowPitcrewPush', ctypes.c_ubyte),
        ('mRepeatShifts', ctypes.c_ubyte),
        ('mHoldClutch', ctypes.c_ubyte),
        ('mAutoReverse', ctypes.c_ubyte),
        ('mAlternateNeutral', ctypes.c_ubyte),
        ('mAIControl', ctypes.c_ubyte),
        ('mUnused1', ctypes.c_ubyte),
        ('mUnused2', ctypes.c_ubyte),
        ('mManualShiftOverrideTime', ctypes.c_float),
        ('mAutoShiftOverrideTime', ctypes.c_float),
        ('mSpeedSensitiveSteering', ctypes.c_float),
        ('mSteerRatioSpeed', ctypes.c_float),
    ]


class rF2TrackedDamage(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mMaxImpactMagnitude', ctypes.c_double),
        ('mAccumulatedImpactMagnitude', ctypes.c_double),
    ]


class rF2VehScoringCapture(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mID', ctypes.c_int),
        ('mPlace', ctypes.c_ubyte),
        ('mIsPlayer', ctypes.c_ubyte),
        ('mFinishStatus', ctypes.c_ubyte),
    ]


class rF2SessionTransitionCapture(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mGamePhase', ctypes.c_ubyte),
        ('mSession', ctypes.c_int),
        ('mNumScoringVehicles', ctypes.c_int),
        ('mScoringVehicles', rF2VehScoringCapture * rFactor2Constants.MAX_MAPPED_VEHICLES),
    ]


class rF2Telemetry(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mVersionUpdateBegin', ctypes.c_int),              # 쓰기 시작 직전 증가
        ('mVersionUpdateEnd', ctypes.c_int),                # 쓰기 완료 후 증가
        ('mBytesUpdatedHint', ctypes.c_int),
        ('mNumVehicles', ctypes.c_int),
        ('mVehicles', rF2VehicleTelemetry * rFactor2Constants.MAX_MAPPED_VEHICLES),
    ]


class rF2Scoring(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mVersionUpdateBegin', ctypes.c_int),
        ('mVersionUpdateEnd', ctypes.c_int),
        ('mBytesUpdatedHint', ctypes.c_int),
        ('mScoringInfo', rF2ScoringInfo),
        ('mVehicles', rF2VehicleScoring * rFactor2Constants.MAX_MAPPED_VEHICLES),
    ]


class rF2Extended(ctypes.Structure):
    _pack_ = 4
    _fields_ = [
        ('mVersionUpdateBegin', ctypes.c_int),
        ('mVersionUpdateEnd', ctypes.c_int),
        ('mVersion', ctypes.c_ubyte * 12),                  # 플러그인 API 버전 문자열
        ('is64bit', ctypes.c_ubyte),
        ('mPhysics', rF2PhysicsOptions),
        ('mTrackedDamages', rF2TrackedDamage * rFactor2Constants.MAX_MAPPED_IDS),
        ('mInRealtimeFC', ctypes.c_ubyte),
        ('mMultimediaThreadStarted', ctypes.c_ubyte),
        ('mSimulationThreadStarted', ctypes.c_ubyte),
        ('mSessionStarted', ctypes.c_ubyte),
        ('mTicksSessionStarted', ctypes.c_double),
        ('mTicksSessionEnded', ctypes.c_double),
        ('mSessionTransitionCapture', rF2SessionTransitionCapture),
        ('mDisplayedMessageUpdateCapture', ctypes.c_ubyte * 128),
        ('mDirectMemoryAccessEnabled', ctypes.c_ubyte),
        ('mTicksStatusMessageUpdated', ctypes.c_double),
        ('mStatusMessage', ctypes.c_ubyte * rFactor2Constants.MAX_STATUS_MSG_LEN),
        ('mTicksLastHistoryMessageUpdated', ctypes.c_double),
        ('mLastHistoryMessage', ctypes.c_ubyte * rFactor2Constants.MAX_STATUS_MSG_LEN),
        ('mCurrentPitSpeedLimit', ctypes.c_float),          # m/s
        ('mSCRPluginEnabled', ctypes.c_ubyte),
        ('mSCRPluginDoubleFileType', ctypes.c_int),
        ('mTicksLSIPhaseMessageUpdated', ctypes.c_double),
        ('mLSIPhaseMessage', ctypes.c_ubyte * rFactor2Constants.MAX_RULES_INSTRUCTION_MSG_LEN),
        ('mTicksLSIPitStateMessageUpdated', ctypes.c_double),
        ('mLSIPitStateMessage', ctypes.c_ubyte * rFactor2Constants.MAX_RULES_INSTRUCTION_MSG_LEN),
        ('mTicksLSIOrderInstructionMessageUpdated', ctypes.c_double),
        ('mLSIOrderInstructionMessage', ctypes.c_ubyte * rFactor2Constants.MAX_RULES_INSTRUCTION_MSG_LEN),
        ('mTicksLSIRulesInstructionMessageUpdated', ctypes.c_double),
        ('mLSIRulesInstructionMessage', ctypes.c_ubyte * rFactor2Constants.MAX_RULES_INSTRUCTION_MSG_LEN),
        ('mUnsubscribedBuffersMask', ctypes.c_int),
        ('mHWControlInputEnabled', ctypes.c_ubyte),
        ('mWeatherControlInputEnabled', ctypes.c_ubyte),
        ('mRulesControlInputEnabled', ctypes.c_ubyte),
    ]


def cbytes_to_str(arr) -> str:
    """ctypes ubyte 배열을 널 종료 문자열로 디코드."""
    raw = bytes(arr)
    return raw.partition(b'\0')[0].decode('utf-8', errors='replace').strip()
