import { HttpContextToken } from '@angular/common/http';

// Owner operations under /guest/groups require the signed-in owner's token.
export const OWNER_GROUP_REQUEST = new HttpContextToken<boolean>(() => false);
