import type {
  OwnershipConfig,
  OwnershipManagementGroupSyncResult,
  SubscriptionOwnership,
  TeamOwnership,
  UpdateSubscriptionOwnershipPayload,
} from '../types/api';
import { api } from './api';

export const ownershipService = {
  getTeams: (): Promise<OwnershipConfig> => api.getOwnershipTeams(),

  getTeam: (teamId: string): Promise<TeamOwnership> => api.getOwnershipTeam(teamId),

  addSubscription: (teamId: string, payload: SubscriptionOwnership) =>
    api.addOwnershipSubscription(teamId, payload),

  updateSubscription: (teamId: string, subscriptionId: string, payload: UpdateSubscriptionOwnershipPayload) =>
    api.updateOwnershipSubscription(teamId, subscriptionId, payload),

  deleteSubscription: (teamId: string, subscriptionId: string) =>
    api.deleteOwnershipSubscription(teamId, subscriptionId),

  syncFromManagementGroups: (): Promise<OwnershipManagementGroupSyncResult> =>
    api.syncOwnershipFromManagementGroups(),

  acceptManagementGroupOverrides: (subscriptionIds: string[]) =>
    api.acceptOwnershipManagementGroupOverrides(subscriptionIds),
};
