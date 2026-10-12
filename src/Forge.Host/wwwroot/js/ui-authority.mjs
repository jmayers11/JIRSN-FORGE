// Control visibility follows business-action grants. The server still authorizes every request.
const scopeValue = (scopeType, resource) => ({ program: resource.programId, facility: resource.facilityId,
  work_center: resource.workCenterCode, contract: resource.contractId, customer: resource.customerId, shift: resource.shiftId })[scopeType];
const matches = (scope, resource) => resource == null || (scopeValue(scope.scopeType, resource) != null
  && String(scopeValue(scope.scopeType, resource)).toLowerCase() === String(scope.scopeId).toLowerCase());
const withinThreshold = (threshold, resource) => resource == null || !threshold || (Number.isFinite(resource.amount)
  && resource.amount <= threshold.maximumAmount && resource.currency?.toUpperCase() === threshold.currency?.toUpperCase());

export function canAction(me, permission, resource = null, now = Date.now()) {
  if (!me) return false;
  const direct = me.permissions?.includes(permission)
    || me.scopedPermissions?.some(p => p.permission === permission && matches(p, resource));
  const lent = me.delegatedPermissions?.some(p => p.permission === permission
    && Date.parse(p.startsAt) <= now && now < Date.parse(p.expiresAt)
    && (p.scopes ?? []).every(scope => matches(scope, resource)) && withinThreshold(p.sourceThreshold, resource));
  if (!direct && !lent) return false;
  const threshold = me.approvalThresholds?.find(p => p.permission === permission);
  // A context-free control opens a form; amounts/scopes are authorized after its inputs are known.
  return withinThreshold(threshold, resource);
}
