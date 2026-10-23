import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import {
  allItems, components, customers, operationalLocations, products, scenarioCatalog,
  subassemblyBoms, subassemblyRoutes, suppliers, createDemoWorkload,
} from '../../tools/whiskerworks-commercial.mjs';
import { machines, nonMachineStations, workCenters } from '../../tools/whiskerworks-plant.mjs';

test('WhiskerWorks commercial manifest matches the fictional factory scale', () => {
  assert.equal(components.length, 95);
  assert.equal(allItems.length, 100);
  assert.equal(products.length, 5);
  assert.equal(suppliers.length, 10);
  assert.equal(customers.length, 10);
  assert.equal(operationalLocations.length, 8);
  assert.equal(scenarioCatalog.length, 16);
  assert.equal(new Set(allItems.map(item => item.partNumber)).size, allItems.length);
});

test('fixed WhiskerWorks support stations are typed non-machine assets in valid work centers', () => {
  const machineCodes = new Set(machines.map(machine => machine.code));
  const centerCodes = new Set(workCenters.map(center => center.code));
  const stationCodes = nonMachineStations.map(station => station.code);
  assert.equal(new Set(stationCodes).size, nonMachineStations.length);
  assert.equal(nonMachineStations.length, 14);
  for (const station of nonMachineStations) {
    assert.ok(!machineCodes.has(station.code), `${station.code} must not be a capacity-bearing machine`);
    assert.ok(centerCodes.has(station.workCenterCode), `${station.code} must belong to a known work center`);
    assert.ok(['Building', 'Utility', 'TestEquipment', 'Tooling', 'Other'].includes(station.type), `${station.code} has a valid non-production asset type`);
  }
});

test('every make part has a valid, acyclic multilevel BOM and a machine-qualified route', () => {
  const makeParts = new Set(allItems.filter(item => item.makeBuy === 'Make').map(item => item.partNumber));
  const boms = [...products.map(product => ({ partNumber: product.partNumber, lines: product.bom })), ...subassemblyBoms];
  const routes = [...products.map(product => product.partNumber), ...subassemblyRoutes.map(route => route.partNumber)];
  const byParent = new Map(boms.map(bom => [bom.partNumber, bom.lines]));
  assert.equal(byParent.size, makeParts.size);
  assert.deepEqual(new Set(routes), makeParts);
  const known = new Set(allItems.map(item => item.partNumber));
  for (const bom of boms) {
    assert.ok(makeParts.has(bom.partNumber));
    for (const [partNumber, quantity] of bom.lines) {
      assert.ok(known.has(partNumber), `${bom.partNumber} references ${partNumber}`);
      assert.ok(Number.isFinite(quantity) && quantity > 0, `${bom.partNumber} has positive quantity for ${partNumber}`);
    }
  }
  const visiting = new Set();
  const visited = new Set();
  const visit = parent => {
    assert.ok(!visiting.has(parent), `BOM cycle at ${parent}`);
    if (visited.has(parent)) return;
    visiting.add(parent);
    for (const [child] of byParent.get(parent) ?? []) if (byParent.has(child)) visit(child);
    visiting.delete(parent);
    visited.add(parent);
  };
  for (const parent of byParent.keys()) visit(parent);
});

test('workload generation is repeatable and meets open-demand/order counts', () => {
  const first = createDemoWorkload('2026-10-26');
  const second = createDemoWorkload('2026-10-26');
  assert.deepEqual(first, second);
  assert.equal(first.demandLines.length, 28);
  assert.equal(first.productionOrders.length, 42);
  assert.equal(new Set(first.demandLines.map(row => row.sourceKey)).size, 28);
  assert.equal(new Set(first.productionOrders.map(row => row.sourceKey)).size, 42);
  assert.equal(first.productionOrders.filter(row => row.initialState === 'Planned').length, 10);
  assert.equal(first.productionOrders.filter(row => row.initialState === 'Held').length, 4);
});

test('commercial seed defaults to a network-free dry run and requires explicit write targets', () => {
  const dryRun = spawnSync(process.execPath, ['tools/seed-whiskerworks-commercial.mjs', '--base=http://127.0.0.1:1'], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(dryRun.status, 0, dryRun.stderr);
  assert.match(dryRun.stdout, /dry run; no server or database calls made/);
  assert.match(dryRun.stdout, /95 components/);
  assert.match(dryRun.stdout, /28 open demand lines/);
  const writeWithoutReader = spawnSync(process.execPath, ['tools/seed-whiskerworks-commercial.mjs', '--apply', '--base=http://127.0.0.1:1', '--subject=dev-admin'], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(writeWithoutReader.status, 1);
  assert.match(writeWithoutReader.stderr, /explicit --base, --subject and --reader/);
});
