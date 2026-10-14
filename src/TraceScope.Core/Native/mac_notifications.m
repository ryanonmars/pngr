// Independent local notifications bridge; no APNs, service, executable helper, or network access.
#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>
#import <dispatch/dispatch.h>
#include <stdio.h>
#include <string.h>
#include <stdatomic.h>
static atomic_bool cancelled = false;
static BOOL waitForCompletion(dispatch_semaphore_t semaphore, int seconds) {
    for (int i = 0; i < seconds * 10; i++) {
        if (atomic_load(&cancelled)) return NO;
        if (!dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC / 10))) return YES;
    }
    return NO;
}

@interface TraceScopeNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end
@implementation TraceScopeNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
      willPresentNotification:(UNNotification *)notification
        withCompletionHandler:(void (^)(UNNotificationPresentationOptions))handler {
    handler(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList | UNNotificationPresentationOptionSound);
}
@end

static UNUserNotificationCenter *getCenter(char *error, int capacity) {
    if (![NSBundle mainBundle].bundleIdentifier.length) {
        snprintf(error, capacity, "Native notifications require launching the packaged Pngr app bundle.");
        return nil;
    }
    static TraceScopeNotificationDelegate *delegate;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ delegate = [TraceScopeNotificationDelegate new]; });
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    center.delegate = delegate;
    return center;
}

__attribute__((visibility("default"))) int ts_notifications_permission(int request, char *error, int capacity) {
    @autoreleasepool {
        @try {
            UNUserNotificationCenter *center = getCenter(error, capacity);
            if (!center) return 0;
            dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
            __block BOOL allowed = NO;
            __block NSString *detail = nil;
            if (request) {
                [center requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound)
                    completionHandler:^(BOOL granted, NSError *failure) {
                        allowed = granted;
                        detail = failure.localizedDescription;
                        dispatch_semaphore_signal(semaphore);
                    }];
            } else {
                [center getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings *settings) {
                    allowed = settings.authorizationStatus == UNAuthorizationStatusAuthorized || settings.authorizationStatus == UNAuthorizationStatusProvisional;
                    dispatch_semaphore_signal(semaphore);
                }];
            }
            if (!waitForCompletion(semaphore, request ? 30 : 3)) {
                snprintf(error, capacity, "Notification authorization did not finish within 30 seconds."); return 0;
            }
            if (!allowed) snprintf(error, capacity, "%s", detail.UTF8String ?: "Notification permission is disabled. Enable Pngr in macOS notification settings.");
            return allowed ? 1 : 0;
        } @catch (NSException *exception) {
            snprintf(error, capacity, "%s", exception.reason.UTF8String); return 0;
        }
    }
}

__attribute__((visibility("default"))) int ts_notifications_send(const char *title, const char *body, char *error, int capacity) {
    @autoreleasepool {
        @try {
            if (!ts_notifications_permission(0, error, capacity)) return 0;
            UNUserNotificationCenter *center = getCenter(error, capacity);
            UNMutableNotificationContent *content = [UNMutableNotificationContent new];
            content.title = [NSString stringWithUTF8String:title];
            content.body = [NSString stringWithUTF8String:body];
            content.sound = UNNotificationSound.defaultSound;
            UNNotificationRequest *request = [UNNotificationRequest requestWithIdentifier:NSUUID.UUID.UUIDString content:content trigger:nil];
            dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
            __block NSString *detail = nil;
            [center addNotificationRequest:request withCompletionHandler:^(NSError *failure) {
                detail = failure.localizedDescription; dispatch_semaphore_signal(semaphore);
            }];
            if (!waitForCompletion(semaphore, 10)) {
                snprintf(error, capacity, "Notification submission timed out."); return 0;
            }
            if (detail) { snprintf(error, capacity, "%s", detail.UTF8String); return 0; }
            return 1;
        } @catch (NSException *exception) {
            snprintf(error, capacity, "%s", exception.reason.UTF8String); return 0;
        }
    }
}

__attribute__((visibility("default"))) void ts_notifications_shutdown(void) {
    atomic_store(&cancelled, true);
    @autoreleasepool {
        @try {
            if ([NSBundle mainBundle].bundleIdentifier.length) {
                UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
                [center removeAllPendingNotificationRequests];
                center.delegate = nil;
            }
        } @catch (NSException *exception) { }
    }
}
