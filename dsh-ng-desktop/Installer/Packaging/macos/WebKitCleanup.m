#import <Foundation/Foundation.h>
#import <WebKit/WebKit.h>
#include <stdio.h>
#include <string.h>

static int cleanup_webkit_store(NSString *identifier) {
    __block BOOL completed = NO;
    __block int result = 1;
    WKWebsiteDataStore *store = nil;
    if (@available(macOS 14.0, *)) {
        NSUUID *uuid = [[NSUUID alloc] initWithUUIDString:identifier];
        if (uuid == nil) {
            fputs("The WebKit data-store identifier is invalid.\n", stderr);
            return 2;
        }

        store = [WKWebsiteDataStore dataStoreForIdentifier:uuid];
    } else {
        fputs("DSH Desktop requires macOS 14 or later.\n", stderr);
        return 4;
    }

    if (store == nil) {
        fputs("The WebKit data store could not be opened.\n", stderr);
        return 3;
    }

    NSSet<NSString *> *types = [WKWebsiteDataStore allWebsiteDataTypes];
    [store fetchDataRecordsOfTypes:types completionHandler:^(NSArray<WKWebsiteDataRecord *> *records) {
        if (records.count == 0) {
            result = 0;
            completed = YES;
            return;
        }

        [store removeDataOfTypes:types forDataRecords:records completionHandler:^ {
            result = 0;
            completed = YES;
        }];
    }];

    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:30.0];
    while (!completed && [deadline timeIntervalSinceNow] > 0) {
        @autoreleasepool {
            [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode
                                     beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
        }
    }

    if (!completed) {
        fputs("Timed out while clearing WebKit data.\n", stderr);
    }
    return result;
}

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSString *identifier = nil;
        for (int index = 1; index + 1 < argc; index++) {
            if (strcmp(argv[index], "--identifier") == 0) {
                identifier = [NSString stringWithUTF8String:argv[index + 1]];
                index++;
            }
        }

        if (identifier == nil) {
            fputs("Usage: DshDesktop.WebKitCleanup --identifier <UUID>\n", stderr);
            return 2;
        }

        return cleanup_webkit_store(identifier);
    }
}
