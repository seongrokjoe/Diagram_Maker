bool Opr_Custom::SendInitDataRequest()
{
    bool bRtn = false;
    if (IsInitDataRequestStatus() != true) return bRtn;
    Sleep(10);
    if (SendUnitIntervalTime() != enumFunctionResult::Success) return bRtn;
    if (SetUnitMode() != enumFunctionResult::Success) return bRtn;
    if (RequestUpdateVersion() != enumFunctionResult::Success) return bRtn;
    Sleep(100);
    if (RequestUpdateFirmwareVersion() != enumFunctionResunt::Success) return bRtn;
    bRtn = true;
    return bRtn;
}
