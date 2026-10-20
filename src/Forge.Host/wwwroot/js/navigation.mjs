/** Return only navigation groups with at least one route the person may open. */
export function filterNavGroups(groups, access, hasPermission, showAll = false) {
  return groups.map(([label, routes]) => [label, routes.filter(route => showAll
    || !(access[route]?.length)
    || access[route].some(permission => hasPermission(permission)))])
    .filter(([, routes]) => routes.length > 0);
}
