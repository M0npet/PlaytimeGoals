<template>
  <fieldset class="config-category form-group playtime-goals">
    <legend class="form-group__legend">Playtime Goals</legend>

    <div class="playtime-goals__header">
      <div>
        <h3 class="playtime-goals__title">Playtime Goals</h3>
        <p class="playtime-goals__subtitle">
          Bot: {{ bot.viewableName || bot.name }} · Managed by PlaytimeGoals plugin
        </p>
      </div>

      <div class="playtime-goals__live" :class="{ 'playtime-goals__live--on': live }">
        <span class="playtime-goals__live-dot"></span>
        <span>{{ live ? 'Live' : 'Paused' }}</span>
      </div>
    </div>

    <div class="playtime-goals__status-grid">
      <div class="playtime-goals__status-card playtime-goals__status-card--control">
        <div class="playtime-goals__status-label">Enabled</div>
        <div class="form-item__buttons">
          <button
            class="button"
            :class="{ 'button--active': enabled }"
            @click.prevent="setEnabled(true)"
          >
            ✔
          </button>

          <button
            class="button"
            :class="{ 'button--cancel': !enabled }"
            @click.prevent="setEnabled(false)"
          >
            ✖
          </button>
        </div>
      </div>

      <div class="playtime-goals__status-card">
        <div class="playtime-goals__status-label">Steam</div>
        <strong>{{ connected ? 'Connected' : 'Offline' }}</strong>
      </div>

      <div class="playtime-goals__status-card">
        <div class="playtime-goals__status-label">Family View</div>
        <strong>{{ familyViewText }}</strong>
      </div>

      <div class="playtime-goals__status-card">
        <div class="playtime-goals__status-label">Family members</div>
        <strong>{{ familyMemberCount }}</strong>
      </div>

      <div class="playtime-goals__status-card">
        <div class="playtime-goals__status-label">Account</div>
        <strong>{{ accountText }}</strong>
      </div>

      <label class="playtime-goals__status-card playtime-goals__status-card--control">
        <span class="playtime-goals__status-label">Concurrent games</span>
        <input
          class="form-item__input playtime-goals__batch"
          type="number"
          min="1"
          max="32"
          step="1"
          :value="batchSize"
          @input="setBatchSize($event.target.value)"
        >
      </label>

      <div class="playtime-goals__status-card">
        <div class="playtime-goals__status-label">Parental policy</div>
        <strong>Automatic</strong>
      </div>
    </div>

    <div class="playtime-goals__toolbar">
      <input
        v-model.trim="query"
        class="form-item__input playtime-goals__search"
        type="search"
        placeholder="Search by game name or AppID"
      >

      <div class="playtime-goals__filters">
        <button
          v-for="item in filters"
          :key="item.value"
          class="button button--small"
          :class="{ 'button--active': filter === item.value }"
          @click.prevent="filter = item.value"
        >
          {{ item.label }}
        </button>
      </div>
    </div>

    <p v-if="errorText" class="playtime-goals__error">
      {{ errorText }}
    </p>

    <div v-if="libraryLoading && !library" class="playtime-goals__loading">
      <FontAwesomeIcon icon="spinner" spin></FontAwesomeIcon>
      <span>Loading Steam library…</span>
    </div>

    <div v-else class="playtime-goals__table-wrap">
      <table class="playtime-goals__table">
        <thead>
          <tr>
            <th class="playtime-goals__check-column">✓</th>
            <th>Game</th>
            <th>Source</th>
            <th>Current</th>
            <th>Target</th>
            <th>Remaining</th>
            <th>Availability</th>
            <th>Status</th>
          </tr>
        </thead>

        <tbody>
          <tr
            v-for="row in filteredRows"
            :key="row.appId"
            :class="{ 'playtime-goals__row--selected': row.selected }"
          >
            <td class="playtime-goals__check-column">
              <input
                type="checkbox"
                :checked="row.selected"
                :disabled="!row.selectable && !row.selected"
                :title="!row.selectable && !row.selected
                  ? 'This game cannot be idled from this account'
                  : ''"
                @change="toggleSelected(row.appId, $event.target.checked)"
              >
            </td>

            <td class="playtime-goals__game">
              <strong>{{ row.name }}</strong>
              <small>AppID {{ row.appId }}</small>
            </td>

            <td>
              <span
                class="playtime-goals__pill"
                :class="`playtime-goals__pill--${row.sourceClass}`"
              >
                {{ row.sourceLabel }}
              </span>
            </td>

            <td>{{ formatHours(row.currentHours) }} h</td>

            <td>
              <input
                v-if="row.selected"
                class="form-item__input playtime-goals__target"
                type="number"
                min="0.1"
                step="0.1"
                :value="goalValue(row.appId)"
                :disabled="!row.selectable"
                placeholder="∞"
                @input="setGoal(row.appId, $event.target.value)"
              >

              <span v-else class="playtime-goals__muted">—</span>
            </td>

            <td>
              <template v-if="row.selected">
                {{
                  row.targetHours == null
                    ? '—'
                    : `${formatHours(row.remainingHours)} h`
                }}
              </template>

              <span v-else class="playtime-goals__muted">—</span>
            </td>

            <td>
              <span
                class="playtime-goals__availability"
                :class="{ 'playtime-goals__availability--bad': !row.available }"
              >
                {{ row.availabilityText }}
              </span>
            </td>

            <td>
              <span
                class="playtime-goals__status-pill"
                :class="`playtime-goals__status-pill--${row.statusClass}`"
              >
                {{ row.statusText }}
              </span>
            </td>
          </tr>

          <tr v-if="!filteredRows.length">
            <td colspan="8" class="playtime-goals__empty">
              No games match the current search and filters.
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="playtime-goals__summary">
      <div>
        <strong>Selected Goals</strong>
        <span class="playtime-goals__muted">
          {{ selectedAppIds.length }} game{{ selectedAppIds.length === 1 ? '' : 's' }}
        </span>
      </div>

      <div v-if="selectedAppIds.length" class="playtime-goals__selected-list">
        <span
          v-for="appId in selectedAppIds"
          :key="appId"
          class="playtime-goals__selected-chip"
        >
          {{ appId }}
        </span>
      </div>

      <p class="playtime-goals__hint">
        GamesPlayedWhileIdle is managed automatically and kept empty.
        Leave Target empty for unlimited idling.
      </p>
    </div>
  </fieldset>
</template>

<script>
export default {
  name: 'PlaytimeGoals',

  props: {
    bot: {
      type: Object,
      required: true,
    },

    model: {
      type: Object,
      required: true,
    },
  },

  data() {
    return {
      status: null,
      library: null,
      parental: null,
      statusLoading: false,
      libraryLoading: false,
      parentalLoading: false,
      query: '',
      filter: 'all',
      statusTimer: null,
      libraryTimer: null,
      parentalTimer: null,
      errorText: '',
      filters: [
        { value: 'all', label: 'All' },
        { value: 'selected', label: 'Selected' },
        { value: 'own', label: 'Own' },
        { value: 'family', label: 'Family' },
        { value: 'free', label: 'Free' },
        { value: 'excluded', label: 'Excluded' },
        { value: 'unavailable', label: 'Unavailable' },
      ],
    };
  },

  computed: {
    enabled() {
      return this.model.PlaytimeGoalsEnabled === true;
    },

    batchSize() {
      const value = Number(this.model.PlaytimeGoalsBatchSize);

      return (
        Number.isInteger(value) &&
        value >= 1 &&
        value <= 32
      )
        ? value
        : 5;
    },

    goals() {
      return this.model.PlaytimeGoals || {};
    },

    selectedAppIds() {
      return Object.keys(this.goals)
        .map(value => Number(value))
        .filter(appId => Number.isInteger(appId) && appId > 0)
        .sort((a, b) => a - b);
    },

    connected() {
      return Boolean(this.status && this.status.Connected);
    },

    live() {
      return (
        document.visibilityState === 'visible' &&
        this.connected
      );
    },

    familyMemberCount() {
      if (!this.library) return '—';
      return Number(this.library.FamilyMemberCount || 0);
    },

    familyViewText() {
      if (!this.parental) return '—';
      if (!this.parental.Available) return 'Unavailable';
      return this.parental.Enabled ? 'Enabled' : 'Disabled';
    },

    accountText() {
      if (!this.status || !this.status.Connected) return 'Offline';
      if (this.status.Farming) return 'ASF farming';
      if (this.status.FarmerPaused) return 'Paused';
      return this.status.PlayingPossible ? 'Free' : 'In use';
    },

    statusByApp() {
      const result = {};

      if (this.status && Array.isArray(this.status.Games)) {
        this.status.Games.forEach(game => {
          result[String(game.AppId)] = game;
        });
      }

      return result;
    },

    rows() {
      if (!this.library || !Array.isArray(this.library.Games)) {
        return [];
      }

      return this.library.Games
        .map(game => {
          const appId = Number(game.AppId);
          const selected = Object.prototype.hasOwnProperty.call(
            this.goals,
            String(appId),
          );

          const live = this.statusByApp[String(appId)] || null;
          const targetRaw = selected ? this.goals[String(appId)] : null;
          const targetHours = (
            selected &&
            targetRaw !== null &&
            targetRaw !== ''
          )
            ? Number(targetRaw)
            : null;

          const currentHours = Number(
            (live && live.CurrentHours) ||
            game.CurrentHours ||
            0,
          );

          const effectiveHours = Number(
            (live && live.EffectiveHours) ||
            currentHours,
          );

          const remainingHours = (
            selected &&
            targetHours != null
          )
            ? Math.max(
              0,
              Number(
                (live && live.RemainingHours) != null
                  ? live.RemainingHours
                  : targetHours - effectiveHours,
              ),
            )
            : null;

          const source = String(game.Source || 'family').toLowerCase();
          const available = this.isAvailable(game);
          const selectable = Boolean(
            typeof game.CanSelect === 'boolean'
              ? game.CanSelect
              : source !== 'excluded',
          );
          const state = selected && live ? live.State : 'not-selected';
          const queuePosition = (
            live &&
            live.QueuePosition != null
          )
            ? Number(live.QueuePosition)
            : null;

          return {
            appId,
            name: game.Name || `App ${appId}`,
            selected,
            targetHours,
            currentHours,
            effectiveHours,
            remainingHours,
            source,
            sourceLabel: this.sourceLabel(source),
            sourceClass: source.replace('+', '-'),
            available,
            selectable,
            queuePosition,
            availabilityText: this.availabilityText(game),
            state,
            statusText: this.statusText(
              state,
              game,
              selected,
              queuePosition,
              selectable,
            ),
            statusClass: this.statusClass(
              state,
              game,
              selected,
              selectable,
            ),
          };
        })
        .sort((a, b) => {
          if (a.selected !== b.selected) return a.selected ? -1 : 1;
          return a.name.localeCompare(b.name);
        });
    },

    filteredRows() {
      const q = this.query.toLowerCase();

      return this.rows.filter(row => {
        if (
          q &&
          !row.name.toLowerCase().includes(q) &&
          !String(row.appId).includes(q)
        ) {
          return false;
        }

        switch (this.filter) {
          case 'selected':
            return row.selected;

          case 'own':
            return row.source === 'own';

          case 'family':
            return row.source === 'family';

          case 'free':
            return row.source === 'free';

          case 'excluded':
            return row.source === 'excluded';

          case 'unavailable':
            return !row.available;

          default:
            return true;
        }
      });
    },
  },

  created() {
    if (typeof this.model.PlaytimeGoalsEnabled === 'undefined') {
      this.$set(this.model, 'PlaytimeGoalsEnabled', false);
    }

    if (typeof this.model.PlaytimeGoalsBatchSize === 'undefined') {
      this.$set(this.model, 'PlaytimeGoalsBatchSize', 5);
    }

    if (
      !this.model.PlaytimeGoals ||
      this.model.PlaytimeGoals.constructor !== Object
    ) {
      this.$set(this.model, 'PlaytimeGoals', {});
    }

    // PlaytimeGoals is the only GamesPlayed owner while this feature is used.
    this.$set(this.model, 'GamesPlayedWhileIdle', []);
    this.$set(this.model, 'CustomGamePlayedWhileIdle', null);
  },

  mounted() {
    document.addEventListener('visibilitychange', this.onVisibilityChange);
    this.refreshAll();
    this.startPolling();
  },

  beforeDestroy() {
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    this.stopPolling();
  },

  methods: {
    setEnabled(value) {
      this.$set(this.model, 'PlaytimeGoalsEnabled', value);

      if (value) {
        this.$set(
          this.model,
          'PlaytimeGoalsParentalWritesEnabled',
          true,
        );

        this.$set(this.model, 'GamesPlayedWhileIdle', []);
        this.$set(this.model, 'CustomGamePlayedWhileIdle', null);
      }
    },

    setBatchSize(raw) {
      const value = Math.max(
        1,
        Math.min(
          32,
          parseInt(raw, 10) || 5,
        ),
      );

      this.$set(
        this.model,
        'PlaytimeGoalsBatchSize',
        value,
      );
    },

    toggleSelected(appId, selected) {
      const key = String(appId);

      if (selected) {
        if (!Object.prototype.hasOwnProperty.call(this.goals, key)) {
          this.$set(this.goals, key, null);
        }
      } else {
        this.$delete(this.goals, key);
      }
    },

    goalValue(appId) {
      const value = this.goals[String(appId)];
      return value == null ? '' : value;
    },

    setGoal(appId, raw) {
      const key = String(appId);

      if (raw === '') {
        this.$set(this.goals, key, null);
        return;
      }

      const value = Number(raw);

      if (Number.isFinite(value) && value > 0) {
        this.$set(this.goals, key, value);
      }
    },

    sourceLabel(source) {
      switch (source) {
        case 'own':
          return 'OWN';

        case 'free':
          return 'FREE';

        case 'excluded':
          return 'EXCLUDED';

        default:
          return 'FAMILY';
      }
    },

    isAvailable(game) {
      const source = String(game.Source || '').toLowerCase();

      if (source === 'own') return true;
      if (source === 'free') return false;
      if (source === 'excluded') return false;

      return Boolean(game.Available);
    },

    familyExcludeReasonText(reason) {
      switch (Number(reason)) {
        case 1:
          return 'Publisher disabled Steam Family sharing';

        case 2:
          return 'License is not shareable';

        case 3:
          return 'Free game';

        case 4:
          return 'Private license';

        case 6:
          return 'Unsupported app type';

        case 7:
          return 'Non-refundable DLC';

        case 8:
          return 'Unreleased app';

        case 9:
          return 'Parent app is excluded';

        case 10:
          return 'Package excluded by publisher';

        case 11:
          return 'Special package is not shareable';

        case 12:
          return 'Developer package';

        case 13:
          return 'Free-weekend license';

        case 15:
          return 'Invalid family package';

        case 16:
          return 'Recurring license is not shareable';

        case 17:
          return 'Unsupported license type';

        case 18:
          return 'Master package is not shareable';

        case 19:
          return 'Package contains no shareable apps';

        case 20:
          return 'Payment master license is excluded';

        case 21:
          return 'Family-group payment license is excluded';

        case 22:
          return 'Authorized-device license is excluded';

        case 23:
          return 'Auto-grant payment license is excluded';

        case 24:
          return 'License is pending';

        case 25:
          return 'License has a pending refund';

        case 26:
          return 'Borrowed license cannot be re-shared';

        case 27:
          return 'Auto-grant license is excluded';

        case 28:
          return 'Timed-trial license is excluded';

        case 29:
          return 'Free subscription is not shareable';

        case 30:
          return 'Inactive license';

        default:
          return 'Not shareable through Steam Family';
      }
    },

    availabilityText(game) {
      const source = String(game.Source || '').toLowerCase();

      if (source === 'own') {
        return game.AlsoInFamily
          ? 'Owned · also in Family'
          : 'Owned';
      }

      if (source === 'free') {
        return 'Free license auto-claimed when selected';
      }

      if (source === 'excluded' || !game.Shareable) {
        return this.familyExcludeReasonText(game.ExcludeReason);
      }

      if (!game.FamilyAvailabilityKnown) {
        return 'Family availability unknown';
      }

      const copies = Number(game.FamilyCopies || 0);
      const used = Number(game.FamilyCopiesInUse || 0);
      const free = Math.max(0, copies - used);

      if (free > 0) {
        return `${free}/${copies} cop${copies === 1 ? 'y' : 'ies'} free`;
      }

      return copies > 0
        ? 'All family copies are in use'
        : 'No family copy available';
    },

    statusText(
      state,
      game,
      selected,
      queuePosition,
      selectable,
    ) {
      if (!selected) {
        return selectable
          ? 'Not selected'
          : 'Cannot select';
      }

      switch (state) {
        case 'idling':
          return 'Idling';

        case 'idling-unlimited':
          return 'Idling ∞';

        case 'queued':
          return queuePosition
            ? `Queued #${queuePosition}`
            : 'Queued';

        case 'complete':
          return 'Complete';

        case 'free-license-pending':
          return 'Claiming free license';

        case 'free-license-claim-failed':
          return 'License claim failed · retrying';

        case 'family-not-shareable':
          return 'Cannot idle';

        case 'family-copy-busy':
          return 'Waiting for family copy';

        case 'family-availability-unknown':
          return 'Family availability unknown';

        case 'parental-blocked':
          return 'Parental blocked';

        case 'account-in-use':
          return 'Account in use';

        case 'asf-farming':
          return 'ASF farming';

        case 'asf-paused':
          return 'ASF paused';

        case 'unavailable':
          return 'Unavailable';

        // Backward compatibility with an old status snapshot.
        case 'waiting':
          return 'Queued';

        case 'family-unavailable':
          return 'Waiting for family copy';

        default:
          return 'Waiting for status';
      }
    },

    statusClass(
      state,
      game,
      selected,
      selectable,
    ) {
      if (!selected) {
        return selectable
          ? 'muted'
          : 'bad';
      }

      switch (state) {
        case 'idling':
        case 'idling-unlimited':
          return 'good';

        case 'complete':
          return 'complete';

        case 'family-not-shareable':
        case 'parental-blocked':
        case 'unavailable':
          return 'bad';

        case 'account-in-use':
          return 'info';

        case 'free-license-claim-failed':
          return 'bad';

        case 'free-license-pending':
        case 'family-copy-busy':
        case 'family-availability-unknown':
        case 'queued':
        case 'waiting':
        case 'family-unavailable':
        case 'asf-farming':
        case 'asf-paused':
        default:
          return 'waiting';
      }
    },

    formatHours(value) {
      const number = Number(value || 0);

      if (number >= 1000) return number.toFixed(0);
      if (number >= 100) return number.toFixed(1);
      return number.toFixed(2);
    },

    async refreshStatus() {
      if (
        this.statusLoading ||
        document.visibilityState !== 'visible'
      ) {
        return;
      }

      this.statusLoading = true;

      try {
        this.status = await this.$http.get(
          `playtimegoals/${encodeURIComponent(this.bot.name)}`,
        );

        this.errorText = '';
      } catch (err) {
        this.errorText = err.message;
      } finally {
        this.statusLoading = false;
      }
    },

    async refreshLibrary() {
      if (
        this.libraryLoading ||
        document.visibilityState !== 'visible'
      ) {
        return;
      }

      this.libraryLoading = true;

      try {
        this.library = await this.$http.get(
          `playtimegoals/${encodeURIComponent(this.bot.name)}/library`,
        );

        this.errorText = '';
      } catch (err) {
        this.errorText = err.message;
      } finally {
        this.libraryLoading = false;
      }
    },

    async refreshParental() {
      if (
        this.parentalLoading ||
        document.visibilityState !== 'visible'
      ) {
        return;
      }

      this.parentalLoading = true;

      try {
        this.parental = await this.$http.get(
          `playtimegoals/${encodeURIComponent(this.bot.name)}/parental`,
        );

        this.errorText = '';
      } catch (err) {
        this.errorText = err.message;
      } finally {
        this.parentalLoading = false;
      }
    },

    refreshAll() {
      if (document.visibilityState !== 'visible') return;

      this.refreshStatus();
      this.refreshLibrary();
      this.refreshParental();
    },

    startPolling() {
      this.stopPolling();

      if (document.visibilityState !== 'visible') return;

      this.statusTimer = window.setInterval(
        this.refreshStatus,
        15000,
      );

      this.libraryTimer = window.setInterval(
        this.refreshLibrary,
        60000,
      );

      this.parentalTimer = window.setInterval(
        this.refreshParental,
        300000,
      );
    },

    stopPolling() {
      if (this.statusTimer) window.clearInterval(this.statusTimer);
      if (this.libraryTimer) window.clearInterval(this.libraryTimer);
      if (this.parentalTimer) window.clearInterval(this.parentalTimer);

      this.statusTimer = null;
      this.libraryTimer = null;
      this.parentalTimer = null;
    },

    onVisibilityChange() {
      if (document.visibilityState === 'visible') {
        this.refreshStatus();
        this.refreshLibrary();
        this.startPolling();
      } else {
        this.stopPolling();
      }
    },
  },
};
</script>

<style lang="scss">
.playtime-goals {
  border-top: 1px solid var(--color-border);
  margin-top: 1.25em;
  padding-top: 1em;
}

.playtime-goals__header,
.playtime-goals__toolbar,
.playtime-goals__summary {
  margin-left: 1em;
  margin-right: 1em;
}

.playtime-goals__header {
  align-items: center;
  display: flex;
  gap: 1em;
  justify-content: space-between;
  margin-bottom: 1em;
}

.playtime-goals__title {
  margin: 0;
}

.playtime-goals__subtitle {
  color: var(--color-text-secondary);
  font-size: 0.88em;
  margin: 0.2em 0 0;
}

.playtime-goals__live {
  align-items: center;
  color: var(--color-text-secondary);
  display: inline-flex;
  font-size: 0.85em;
  gap: 0.45em;
}

.playtime-goals__live-dot {
  background: var(--color-text-disabled);
  border-radius: 50%;
  display: inline-block;
  height: 0.65em;
  width: 0.65em;
}

.playtime-goals__live--on .playtime-goals__live-dot {
  background: #32a852;
}

.playtime-goals__status-grid {
  display: grid;
  gap: 0.65em;
  grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
  margin: 0 1em 1em;
}

.playtime-goals__status-card {
  background: var(--color-background-light);
  border: 1px solid var(--color-border);
  border-radius: 5px;
  box-sizing: border-box;
  min-height: 64px;
  padding: 0.65em 0.75em;
}

.playtime-goals__status-card--control {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
}

.playtime-goals__status-label {
  color: var(--color-text-secondary);
  font-size: 0.78em;
  margin-bottom: 0.35em;
}

.playtime-goals__batch {
  max-width: 6em;
}

.playtime-goals__toolbar {
  align-items: center;
  display: flex;
  flex-wrap: wrap;
  gap: 0.65em;
  margin-bottom: 1em;
}

.playtime-goals__search {
  flex: 1 1 260px;
  min-width: 220px;
}

.playtime-goals__filters {
  display: flex;
  flex-wrap: wrap;
  gap: 0.35em;
}

.playtime-goals__table-wrap {
  margin: 0 1em 1em;
  overflow-x: auto;
}

.playtime-goals__table {
  border-collapse: collapse;
  min-width: 920px;
  width: 100%;
}

.playtime-goals__table th,
.playtime-goals__table td {
  border-bottom: 1px solid var(--color-border);
  padding: 0.62em 0.55em;
  text-align: left;
  vertical-align: middle;
}

.playtime-goals__table th {
  color: var(--color-text-secondary);
  font-size: 0.8em;
  font-weight: 600;
  text-transform: uppercase;
}

.playtime-goals__row--selected {
  background: color-mix(in srgb, var(--color-theme) 7%, transparent);
}

.playtime-goals__check-column {
  text-align: center !important;
  width: 28px;
}

.playtime-goals__game {
  min-width: 190px;
}

.playtime-goals__game small {
  color: var(--color-text-secondary);
  display: block;
  font-size: 0.75em;
  margin-top: 0.15em;
}

.playtime-goals__target {
  min-width: 6.5em;
  width: 7em;
}

.playtime-goals__pill,
.playtime-goals__status-pill,
.playtime-goals__selected-chip {
  border: 1px solid var(--color-border);
  border-radius: 999px;
  display: inline-block;
  font-size: 0.75em;
  line-height: 1;
  padding: 0.42em 0.62em;
  white-space: nowrap;
}

.playtime-goals__pill--own {
  border-color: var(--color-theme);
}

.playtime-goals__pill--family {
  border-color: #8b5cf6;
}

.playtime-goals__pill--free {
  border-color: #32a852;
}

.playtime-goals__pill--excluded {
  border-color: var(--color-button-cancel);
  color: var(--color-text-info);
}

.playtime-goals__availability {
  white-space: nowrap;
}

.playtime-goals__availability--bad {
  color: var(--color-text-info);
}

.playtime-goals__status-pill--good {
  border-color: #32a852;
}

.playtime-goals__status-pill--complete {
  border-color: var(--color-theme);
}

.playtime-goals__status-pill--bad {
  border-color: var(--color-button-cancel);
}

.playtime-goals__status-pill--info {
  border-color: var(--color-text-info);
}

.playtime-goals__status-pill--waiting,
.playtime-goals__status-pill--muted {
  color: var(--color-text-secondary);
}

.playtime-goals__summary {
  background: var(--color-background-light);
  border: 1px solid var(--color-border);
  border-radius: 5px;
  margin-bottom: 1em;
  padding: 0.85em;
}

.playtime-goals__selected-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.35em;
  margin-top: 0.65em;
}

.playtime-goals__selected-chip {
  color: var(--color-text-secondary);
}

.playtime-goals__hint,
.playtime-goals__muted {
  color: var(--color-text-secondary);
}

.playtime-goals__hint {
  font-size: 0.84em;
  margin: 0.75em 0 0;
}

.playtime-goals__error {
  color: var(--color-button-cancel);
  margin: 0 1em 1em;
}

.playtime-goals__loading,
.playtime-goals__empty {
  color: var(--color-text-secondary);
  padding: 1.4em;
  text-align: center;
}

@media (max-width: 700px) {
  .playtime-goals__header {
    align-items: flex-start;
    flex-direction: column;
  }

  .playtime-goals__status-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
