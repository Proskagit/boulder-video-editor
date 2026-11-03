namespace AiVideoEditor.Core.Telemetry.Calibration;

/// <summary>
/// What a save reports for one active usable mark (D051 §5.8): its key, the observation <c>c</c> in ticks — null when the
/// video of its effective start has no <c>Z</c> (§5.9, the record is left unchanged) — and the mark's
/// <c>positionChangedAt</c> from the project.
/// </summary>
public readonly record struct CalibrationObservation(CalibrationKey Key, long? Value, DateTimeOffset PositionChangedAt);

/// <summary>The records after a synchronisation and whether anything changed (the store is written only then).</summary>
public sealed record CalibrationSyncResult(IReadOnlyDictionary<CalibrationKey, CalibrationRecord> Records, bool Changed);

/// <summary>
/// The synchronisation of a save with Beta on (D051 §6.4) — pure: the records of the store, the trainings of the project,
/// the observations of their active usable marks and the time of the save give the new records.
/// </summary>
public static class CalibrationSync
{
    public static CalibrationSyncResult Apply(
        IReadOnlyDictionary<CalibrationKey, CalibrationRecord> records,
        IReadOnlySet<TrainingActivity> trainings,
        IReadOnlyCollection<CalibrationObservation> observations,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(trainings);
        ArgumentNullException.ThrowIfNull(observations);

        var result = new Dictionary<CalibrationKey, CalibrationRecord>(records);
        var seen = new HashSet<CalibrationKey>();
        var changed = false;
        foreach (var observation in observations)
        {
            // D051 §5.2: one active mark per climb, so one observation per key.
            if (!seen.Add(observation.Key))
                throw new ArgumentException("Two observations for one climb.", nameof(observations));
            if (!trainings.Contains(observation.Key.Training))
                throw new ArgumentException("An observation of a training that is not in the project.", nameof(observations));
            if (observation.Value is not { } value) continue;         // no Z on the effective start's video: unchanged

            var (status, reason) = StartCalibration.Classify(value);
            if (!result.TryGetValue(observation.Key, out var record))
            {
                result[observation.Key] = new CalibrationRecord(observation.Key, value, status, reason, now, observation.PositionChangedAt, now);
                changed = true;
                continue;
            }

            if (record.Status != CalibrationRecordStatus.Removed && record.Value == value &&
                record.PositionChangedAt == observation.PositionChangedAt && record.Status == status && record.Reason == reason)
                continue;                                             // the same value and position time: nothing written

            result[observation.Key] = record with
            {
                Value = value,
                Status = status,
                Reason = reason,
                PositionChangedAt = observation.PositionChangedAt,
                ValueChangedAt = record.Value == value ? record.ValueChangedAt : now
            };
            changed = true;
        }

        // A used / excluded record of these trainings without an active usable mark becomes a tombstone; the records of a
        // training no longer in the project are never touched (D051 §5.11).
        foreach (var record in records.Values)
        {
            if (record.Status == CalibrationRecordStatus.Removed || seen.Contains(record.Key) ||
                !trainings.Contains(record.Key.Training)) continue;
            result[record.Key] = record with { Status = CalibrationRecordStatus.Removed, Reason = null };
            changed = true;
        }

        return new CalibrationSyncResult(result, changed);
    }
}
